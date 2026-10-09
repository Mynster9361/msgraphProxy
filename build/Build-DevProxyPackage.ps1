<#
.SYNOPSIS
    Builds self-contained Dev Proxy binaries from source and packages them
    as zip files, ready for a msgraphProxy GitHub release.

.DESCRIPTION
    Clones the upstream dotnet/dev-proxy repository into a temporary
    directory, adds this repository's vendored GraphSchemaMockPlugin and
    EntraTokenMockPlugin sources to it, publishes a self-contained build for
    each requested runtime identifier, and zips each one up.

    Also patches dev-proxy's own ProxyEngine.cs: with installCert:false (which
    Start-MsGraphProxy -CI sets on Windows, to avoid an interactive OS
    certificate-trust dialog blocking startup entirely), the underlying proxy
    library (Unobtanium.Web.Proxy) independently calls its own OS-trust
    routine from inside StartAsync() - unbounded, and the very thing
    installCert:false exists to avoid - unless a certificate is already
    assigned to the endpoint at that point. The patch clears that assignment
    again immediately after StartAsync returns, so real proxy traffic still
    gets a correct per-domain leaf certificate instead of one certificate
    served for every host (confirmed directly via a raw TLS handshake this
    used to break). See the inline comment at the patch site for the full
    trail. The patch match is exact and this throws loudly if it doesn't find
    that exact text, rather than silently building a package with broken or
    undiagnosable certificate behavior if upstream dev-proxy has changed that
    file.

    Also patches GraphMinimalPermissionsPlugin.cs, GraphUtils.cs and
    GraphMinimalPermissionsGuidancePlugin.cs: as of the newest tagged release
    at the time of writing, all three still call the now-retired
    devxapi-func-prod-eastus.azurewebsites.net host for minimal Graph
    permissions, which returns an HTML 403 instead of JSON - the exception
    from that aborts the whole report, so Stop-MsGraphProxy's Recording
    silently comes back $null. Patched to the confirmed-working replacement
    host, graph-devx-api.microsoft.com, with the exact same request/response
    shape. This one patch is self-skipping per file once upstream ships the
    same fix in a tagged release (see the inline comment at the patch site).

    This always works against a fresh clone in the temp folder, so the
    original dev-proxy checkout on this machine, if any, is never touched.

.PARAMETER Rid
    One or more .NET runtime identifiers to build for.

.PARAMETER Ref
    Git ref (tag, branch or commit) of dotnet/dev-proxy to build from.
    Overrides -MajorVersion's auto-resolution entirely - pass this to pin
    an exact version (e.g. for reproducing an issue) or to deliberately
    test a new major line. Left unset (the default), the latest v<Major>.*
    tag is resolved automatically every run.

.PARAMETER MajorVersion
    Caps auto-resolution (see -Ref) to this major version of dotnet/dev-proxy,
    picking the newest matching tag (e.g. v3.4.0 over v3.3.1) so bugfixes and
    non-breaking releases are picked up automatically without ever risking an
    unreviewed major-version jump. This matters because this script's patches
    (see the DESCRIPTION above) target exact code in ProxyEngine.cs and
    GraphMockResponsePlugin.cs - as of this writing, main has removed
    ProxyEngine.cs outright in favor of a new Kestrel-based engine, which
    would make that patch throw immediately. Bump this deliberately, after
    re-verifying every patch by hand against the new major version - never
    just let it drift forward on its own.

.PARAMETER OutputPath
    Directory to write the packaged zip files to.

.EXAMPLE
    PS C:\> .\build\Build-DevProxyPackage.ps1

    Builds win-x64, linux-x64 and osx-arm64 packages into .\package.
#>
[CmdletBinding()]
param (
    [string[]]
    # osx-x64 (Intel Mac) isn't included by default: GitHub Actions'
    # macos-latest runners are Apple Silicon, so there's no CI coverage to
    # verify an Intel build actually works - shipping it untested seemed
    # worse than not shipping it. Pass it explicitly if you need it anyway.
    $Rid = @('win-x64', 'linux-x64', 'osx-arm64'),

    [string]
    $Ref,

    [int]
    $MajorVersion = 3,

    [string]
    $OutputPath = (Join-Path -Path $PSScriptRoot -ChildPath '..\package')
)

$ErrorActionPreference = 'Stop'

$pluginsSourceRoot = Join-Path -Path $PSScriptRoot -ChildPath 'plugins-src'
$cloneRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "msgraphproxy-devproxy-$([guid]::NewGuid())"

if (-not (Test-Path -Path $OutputPath)) {
    New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
}

if (-not $Ref) {
    Write-Verbose "Resolving the latest dotnet/dev-proxy v$MajorVersion.x.x tag (pass -Ref to pin an exact version instead)"
    $tags = Invoke-RestMethod -Uri "https://api.github.com/repos/dotnet/dev-proxy/tags?per_page=100" -Headers @{ 'User-Agent' = 'msgraphProxy' }
    $resolvedVersion = $tags.name |
        Where-Object { $_ -match "^v$MajorVersion\.\d+\.\d+$" } |
        ForEach-Object { [version]$_.TrimStart('v') } |
        Sort-Object -Descending |
        Select-Object -First 1
    if (-not $resolvedVersion) {
        throw "No v$MajorVersion.x.x tag found for dotnet/dev-proxy. Pass -Ref explicitly, or -MajorVersion to look for a different major line. Tags seen: $($tags.name -join ', ')"
    }
    $Ref = "v$resolvedVersion"
    Write-Verbose "Resolved to $Ref"
}

try {
    Write-Verbose "Cloning dotnet/dev-proxy@$Ref into $cloneRoot"
    git clone --branch $Ref --depth 1 https://github.com/dotnet/dev-proxy.git $cloneRoot

    Write-Verbose 'Adding msgraphProxy plugin sources'
    $pluginsMockingDir = Join-Path -Path $cloneRoot -ChildPath 'DevProxy.Plugins\Mocking'
    Copy-Item -Path (Join-Path -Path $pluginsSourceRoot -ChildPath '*.cs') -Destination $pluginsMockingDir -Force

    Write-Verbose 'Patching ProxyEngine.cs so installCert:false no longer breaks per-domain certificate generation or hangs on Windows CI'
    $proxyEngineFile = Join-Path -Path $cloneRoot -ChildPath 'DevProxy\Proxy\ProxyEngine.cs'
    $proxyEngineContent = Get-Content -Path $proxyEngineFile -Raw

    # Each patch is whitespace-tolerant (\s+ between tokens, Singleline so .
    # spans the original's line breaks) rather than a literal block match - a
    # literal multi-line here-string turned out to be sensitive to CRLF-vs-LF
    # differences between this file and a freshly git-cloned copy, which
    # defeats the point of failing loudly instead of silently mismatching.
    # Every patch throws if its anchor text isn't found, rather than silently
    # shipping a package with the original (broken, or undiagnosable) behavior
    # if upstream dev-proxy has changed this file.
    #
    # A single patch, not two: an earlier version of this removed the
    # GenericCertificate assignment below outright (to fix
    # RemoteCertificateNameMismatch - a permanently-assigned GenericCertificate
    # makes Unobtanium serve that one cert for every host instead of
    # generating a proper per-domain leaf cert, confirmed via a raw TLS
    # handshake). That reintroduced a *different*, previously-unknown hang on
    # Windows CI specifically, confirmed via diagnostic Console.WriteLine
    # bracketing in a real CI run: ProxyServer.StartAsync() (Unobtanium's own
    # code, not dev-proxy's) independently calls EnsureRootCertificateAsync -
    # its OS-trust attempt, unbounded and identical to the interactive dialog
    # installCert:false exists to avoid - whenever GenericCertificate is null
    # at that point, regardless of dev-proxy's own installCert config.
    # Leaving the assignment in place (as upstream already does) satisfies
    # that internal check without hanging, and clearing it again immediately
    # after StartAsync returns - before Unobtanium's async accept loop
    # (BeginAcceptSocket) could possibly hand it a real connection - restores
    # correct per-domain certificate generation for all actual proxy traffic.
    #
    # Anchored on the StartAsync call alone, not "AddEndPoint immediately
    # followed by StartAsync" - confirmed live that v3.3.1 inserted an
    # unrelated `await ApiSecurity.SaveTokenAsync(stoppingToken);` line
    # between the two (a new control-API auth feature), which broke an
    # earlier version of this patch that required them adjacent. The
    # StartAsync call itself (confirmed to appear exactly once in both
    # v3.2.0 and v3.3.1) is what actually matters for this patch - inserting
    # right after it is correct regardless of what upstream puts before it.
    $patches = @(
        [pscustomobject]@{
            Label       = 'StartAsync call'
            Pattern     = '(?<indent>[ \t]*)await\s+ProxyServer\.StartAsync\(cancellationToken:\s*stoppingToken\);'
            Replacement = "`${indent}await ProxyServer.StartAsync(cancellationToken: stoppingToken);`n" +
                          "`${indent}if (!_config.InstallCert)`n" +
                          "`${indent}{`n" +
                          "`${indent}    _explicitEndPoint.GenericCertificate = null;`n" +
                          "`${indent}}"
        }
    )

    foreach ($patch in $patches) {
        $regex = [regex]::new($patch.Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $regex.IsMatch($proxyEngineContent)) {
            throw "Couldn't find the expected $($patch.Label) in ProxyEngine.cs to patch - upstream dev-proxy may have changed this file. Aborting rather than silently shipping a package with the broken/undiagnosable certificate behavior."
        }
        $proxyEngineContent = $regex.Replace($proxyEngineContent, $patch.Replacement)
    }

    Set-Content -Path $proxyEngineFile -Value $proxyEngineContent -NoNewline

    # GraphMockResponsePlugin (dev-proxy's own built-in mocks.json plugin,
    # not ours) has a real asymmetry between its batch and non-batch
    # request handling: for an ordinary request that matches nothing in
    # mocks.json, it correctly does nothing and lets later plugins (like
    # GraphSchemaMockPlugin) take over - confirmed directly in its source,
    # this is the whole reason schema-based mocking has worked at all
    # everywhere else this session. For a $batch request, though, it
    # unconditionally claims the response regardless of whether ANY
    # sub-request matched a real mocks.json entry, wrapping every
    # unmatched sub-request in a synthetic 502 "No mock response found"
    # placeholder - confirmed directly via a real request, where every
    # $batch call came back entirely 502-filled even with mocks.json
    # completely empty (this module's shipped default), never reaching
    # GraphSchemaMockPlugin's own $batch support at all. The patch makes it
    # fall through instead - exactly mirroring its own non-batch behavior -
    # when literally none of the sub-requests matched anything, while still
    # honoring real mocks.json entries when they exist (a batch with at
    # least one genuine match is left completely alone).
    Write-Verbose "Patching GraphMockResponsePlugin.cs so an empty mocks.json doesn't swallow every `$batch request before GraphSchemaMockPlugin gets a turn"
    $mockResponsePluginFile = Join-Path -Path $cloneRoot -ChildPath 'DevProxy.Plugins\Mocking\GraphMockResponsePlugin.cs'
    $mockResponsePluginContent = Get-Content -Path $mockResponsePluginFile -Raw

    $batchFallthroughPattern = [regex]::new(
        '(?<indent>[ \t]*)var batchRequestId\s*=\s*Guid\.NewGuid\(\)\.ToString\(\);\s*var batchRequestDate\s*=\s*DateTime\.Now\.ToString\("r",\s*CultureInfo\.InvariantCulture\);\s*var batchHeaders\s*=\s*ProxyUtils\.BuildGraphResponseHeaders\(e\.Session\.HttpClient\.Request,\s*batchRequestId,\s*batchRequestDate\);',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $batchFallthroughPattern.IsMatch($mockResponsePluginContent)) {
        throw "Couldn't find the expected `$batch response-building code in GraphMockResponsePlugin.cs to patch - upstream dev-proxy may have changed this file. Aborting rather than silently shipping a package where an empty mocks.json breaks every `$batch request."
    }

    $batchFallthroughReplacement = "`${indent}if (responses.TrueForAll(r => r.Status == (int)HttpStatusCode.BadGateway))`n" +
                                    "`${indent}{`n" +
                                    "`${indent}    return;`n" +
                                    "`${indent}}`n`n" +
                                    "`${indent}var batchRequestId = Guid.NewGuid().ToString();`n" +
                                    "`${indent}var batchRequestDate = DateTime.Now.ToString(`"r`", CultureInfo.InvariantCulture);`n" +
                                    "`${indent}var batchHeaders = ProxyUtils.BuildGraphResponseHeaders(e.Session.HttpClient.Request, batchRequestId, batchRequestDate);"
    $mockResponsePluginContent = $batchFallthroughPattern.Replace($mockResponsePluginContent, $batchFallthroughReplacement)
    Set-Content -Path $mockResponsePluginFile -Value $mockResponsePluginContent -NoNewline

    # GraphMinimalPermissionsPlugin (and its delegated-scope helper GraphUtils,
    # and the guidance-only GraphMinimalPermissionsGuidancePlugin) call a live
    # Microsoft-hosted API to turn recorded requests into a minimal-permissions
    # report. As of v3.3.1 (the newest tagged release at the time of writing)
    # all three still hardcode the API's old hostname,
    # devxapi-func-prod-eastus.azurewebsites.net, which now returns an HTML
    # "403 Ip Forbidden" page instead of JSON - confirmed directly, this is
    # what makes Stop-MsGraphProxy's Recording come back $null with no
    # diagnostic (GraphMinimalPermissionsPlugin throws a JsonException trying
    # to parse the HTML, aborting its report before it's ever written to
    # disk). The replacement host, graph-devx-api.microsoft.com, is confirmed
    # live with the exact same request/response shape - same query string,
    # same POST body, same result JSON. Upstream already fixed this the same
    # way on main (dotnet/dev-proxy#1900, merged after v3.3.1) - once that
    # lands in a tagged v3.x release, -MajorVersion's auto-resolution picks it
    # up on its own and the old host genuinely won't be there to find, so a
    # missing anchor is treated as "already fixed upstream, nothing to do"
    # rather than a hard failure - unlike the two structural patches above,
    # where a missing anchor means upstream changed something unexpectedly.
    Write-Verbose 'Patching the retired devxapi-func-prod-eastus.azurewebsites.net host to graph-devx-api.microsoft.com (skipped per-file if upstream already fixed it)'
    $oldPermissionsHost = 'devxapi-func-prod-eastus.azurewebsites.net'
    $newPermissionsHost = 'graph-devx-api.microsoft.com'
    $permissionsHostFiles = @(
        'DevProxy.Plugins\Reporting\GraphMinimalPermissionsPlugin.cs'
        'DevProxy.Plugins\Reporting\GraphMinimalPermissionsGuidancePlugin.cs'
        'DevProxy.Plugins\Utils\GraphUtils.cs'
    )
    foreach ($relativePath in $permissionsHostFiles) {
        $filePath = Join-Path -Path $cloneRoot -ChildPath $relativePath
        $content = Get-Content -Path $filePath -Raw
        if ($content -notlike "*$oldPermissionsHost*") {
            if ($content -like "*$newPermissionsHost*") {
                Write-Verbose "$relativePath already uses $newPermissionsHost - upstream has fixed this one, nothing to patch."
                continue
            }
            throw "Neither '$oldPermissionsHost' nor '$newPermissionsHost' found in $relativePath - upstream dev-proxy may have changed this file differently than expected. Aborting rather than silently shipping a package pointed at an unknown host."
        }
        $content = $content.Replace($oldPermissionsHost, $newPermissionsHost)
        Set-Content -Path $filePath -Value $content -NoNewline
    }

    foreach ($currentRid in $Rid) {
        Write-Verbose "Publishing devproxy for $currentRid"
        $publishDir = Join-Path -Path $cloneRoot -ChildPath "dist\$currentRid"
        $devProxyProject = Join-Path -Path $cloneRoot -ChildPath 'DevProxy\DevProxy.csproj'

        dotnet publish $devProxyProject -c Release -r $currentRid --self-contained true -o $publishDir

        Write-Verbose "Building plugins for $currentRid"
        $pluginsProject = Join-Path -Path $cloneRoot -ChildPath 'DevProxy.Plugins\DevProxy.Plugins.csproj'
        dotnet build $pluginsProject -c Release -r $currentRid --no-self-contained

        $builtPluginsDir = Join-Path -Path $cloneRoot -ChildPath "DevProxy\bin\Release\net10.0\$currentRid\plugins"
        $publishedPluginsDir = Join-Path -Path $publishDir -ChildPath 'plugins'
        Copy-Item -Path $builtPluginsDir -Destination $publishedPluginsDir -Recurse -Force

        $zipPath = Join-Path -Path $OutputPath -ChildPath "msgraphproxy-devproxy-$currentRid.zip"
        if (Test-Path -Path $zipPath) {
            Remove-Item -Path $zipPath -Force
        }
        Compress-Archive -Path (Join-Path -Path $publishDir -ChildPath '*') -DestinationPath $zipPath

        Write-Verbose "Packaged $currentRid to $zipPath"
    }
}
finally {
    if (Test-Path -Path $cloneRoot) {
        Remove-Item -Path $cloneRoot -Recurse -Force
    }
}
