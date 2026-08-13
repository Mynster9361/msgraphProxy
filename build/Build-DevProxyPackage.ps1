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

    This always works against a fresh clone in the temp folder, so the
    original dev-proxy checkout on this machine, if any, is never touched.

.PARAMETER Rid
    One or more .NET runtime identifiers to build for.

.PARAMETER Ref
    Git branch of dotnet/dev-proxy to build from.

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
    $Ref = 'main',

    [string]
    $OutputPath = (Join-Path -Path $PSScriptRoot -ChildPath '..\package')
)

$ErrorActionPreference = 'Stop'

$pluginsSourceRoot = Join-Path -Path $PSScriptRoot -ChildPath 'plugins-src'
$cloneRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "msgraphproxy-devproxy-$([guid]::NewGuid())"

if (-not (Test-Path -Path $OutputPath)) {
    New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
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
    $patches = @(
        [pscustomobject]@{
            Label       = 'AddEndPoint/StartAsync call'
            Pattern     = '(?<indent>[ \t]*)ProxyServer\.AddEndPoint\(_explicitEndPoint\);\s*await\s+ProxyServer\.StartAsync\(cancellationToken:\s*stoppingToken\);'
            Replacement = "`${indent}ProxyServer.AddEndPoint(_explicitEndPoint);`n" +
                          "`${indent}await ProxyServer.StartAsync(cancellationToken: stoppingToken);`n" +
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
