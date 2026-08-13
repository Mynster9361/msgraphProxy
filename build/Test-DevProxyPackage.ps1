<#
.SYNOPSIS
    Smoke-tests a packaged Dev Proxy build by actually running it and driving
    real Microsoft Graph requests through it.

.DESCRIPTION
    Extracts a packaged zip (as produced by Build-DevProxyPackage.ps1) into the
    msgraphProxy module's local binary cache, then uses the module itself -
    Start-MsGraphProxy / Stop-MsGraphProxy - to launch it and exercise the
    GraphSchemaMockPlugin and EntraTokenMockPlugin extensions this repo ships:
    plain CRUD, $ref, $value, $count, $filter, $expand, a bound Function, a
    bound Action, and the mocked token endpoint. Intended to run in CI between
    building a package and publishing it as a release, so a broken build never
    gets published.

    Only meaningful against a package matching the current OS - a linux-x64
    build can't be executed on a Windows runner, so this only self-tests
    whichever RID matches the machine it runs on.

.PARAMETER PackagePath
    Path to the packaged zip file, e.g. .\package\msgraphproxy-devproxy-win-x64.zip.

.EXAMPLE
    PS C:\> .\build\Test-DevProxyPackage.ps1 -PackagePath .\package\msgraphproxy-devproxy-win-x64.zip

    Installs and smoke-tests that package, throwing if any check fails.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory)]
    [string]
    $PackagePath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -Path $PackagePath)) {
    throw "Package not found: $PackagePath"
}

$moduleManifest = Join-Path -Path $PSScriptRoot -ChildPath '..\msgraphProxy\msgraphProxy.psd1'
Remove-Module msgraphProxy -ErrorAction Ignore
Import-Module $moduleManifest -Force

$module = Get-Module msgraphProxy
$rid = & $module { Get-MsGraphProxyRid }
$binRoot = & $module { $script:MsGraphProxyBinRoot }
$ridRoot = Join-Path -Path $binRoot -ChildPath $rid
$devProxyStdOutLog = & $module { $script:MsGraphProxyStdOutLog }
$devProxyStdErrLog = & $module { $script:MsGraphProxyStdErrLog }

Write-Host "Installing $PackagePath as the $rid build"
if (Test-Path -Path $ridRoot) {
    Remove-Item -Path $ridRoot -Recurse -Force
}
New-Item -Path $ridRoot -ItemType Directory -Force | Out-Null
Expand-Archive -Path $PackagePath -DestinationPath $ridRoot -Force
if ($IsLinux -or $IsMacOS) {
    & chmod +x (Join-Path -Path $ridRoot -ChildPath 'devproxy')
}

$failures = [System.Collections.Generic.List[string]]::new()

function Test-Check {
    param (
        [string]
        $Name,

        [scriptblock]
        $Test
    )

    Write-Host "  - $Name" -NoNewline
    try {
        & $Test
        Write-Host ' OK' -ForegroundColor Green
    } catch {
        Write-Host " FAILED: $_" -ForegroundColor Red
        $script:failures.Add("${Name}: $_")
    }
}

# Dev Proxy needs a moment after Start-Process to actually bind its proxy
# port, so the very first request through it gets a short retry loop rather
# than a fixed sleep, to avoid CI flakiness on top of what Start-MsGraphProxy
# -CI already waits for internally (its control API). The last caught
# exception is surfaced in the final throw - silently retrying without
# keeping it would leave a timeout with no clue *why* it never became ready.
function Wait-ProxyReady {
    param (
        [hashtable]
        $Headers,

        [int]
        $TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $Headers -SkipCertificateCheck -TimeoutSec 5 | Out-Null
            return
        } catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }
    }

    throw "Dev Proxy did not become ready to serve HTTPS requests within $TimeoutSeconds seconds. Last error: $lastError"
}

try {
    Write-Host 'Starting Dev Proxy (-CI)'
    $ciResult = Start-MsGraphProxy -CI -Confirm:$false
    if (-not $ciResult.Running) {
        throw 'Dev Proxy did not report as running after Start-MsGraphProxy -CI.'
    }
    Write-Host "  CertificateTrusted: $($ciResult.CertificateTrusted) (informational - see Known risk notes in the plan; not a hard failure on its own)"

    # -SkipCertificateCheck stays regardless of CertificateTrusted, since
    # whether automatic trust actually succeeds in a given CI run is exactly
    # the thing this script can't guarantee (see Known risk notes) - it can't
    # be allowed to fail every other check over that specifically. No -Proxy
    # is needed on any of these calls though: -CI already points the current
    # process at Dev Proxy via env vars and a direct HttpClient.DefaultProxy
    # override (see Start-MsGraphProxy's help for why the override matters).
    $headers = @{ Authorization = 'Bearer faketoken' }
    Wait-ProxyReady -Headers $headers

    Test-Check 'GET /users returns a mocked collection' {
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if ($r.value.Count -lt 1) { throw "expected at least 1 user, got $($r.value.Count)" }
    }

    $userId = (Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers -SkipCertificateCheck -TimeoutSec 15).value[0].id

    Test-Check 'GET /users/{id} returns the matching item' {
        $r = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/users/$userId" -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if ($r.id -ne $userId) { throw "expected id $userId, got $($r.id)" }
    }

    Test-Check 'POST .../members/$ref adds a reference (204)' {
        $body = @{ '@odata.id' = 'https://graph.microsoft.com/v1.0/directoryObjects/11111111-1111-1111-1111-111111111111' } | ConvertTo-Json
        $r = Invoke-WebRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/groups/22222222-2222-2222-2222-222222222222/members/`$ref" -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        if ($r.StatusCode -ne 204) { throw "expected 204, got $($r.StatusCode)" }
    }

    Test-Check 'POST .../checkMemberGroups (bound Action) returns a value array' {
        $body = @{ groupIds = @('33333333-3333-3333-3333-333333333333') } | ConvertTo-Json
        $r = Invoke-RestMethod -Method POST -Uri "https://graph.microsoft.com/v1.0/users/$userId/checkMemberGroups" -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.value) { throw 'expected a value array in the response' }
    }

    Test-Check 'GET /applications/delta (bound Function) resolves' {
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/applications/delta' -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.value) { throw 'expected a value array in the response' }
    }

    Test-Check 'GET .../mail/$value returns raw text' {
        $r = Invoke-WebRequest -Uri "https://graph.microsoft.com/v1.0/users/$userId/mail/`$value" -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if ($r.Headers['Content-Type'] -notlike 'text/plain*') { throw "expected text/plain, got $($r.Headers['Content-Type'])" }
    }

    Test-Check 'GET /users/$count returns a bare integer' {
        $r = Invoke-WebRequest -Uri 'https://graph.microsoft.com/v1.0/users/$count' -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if ($r.Content -notmatch '^\d+$') { throw "expected a bare integer, got '$($r.Content)'" }
    }

    Test-Check '$filter narrows results' {
        $r = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/users?`$filter=givenName eq 'Jane'" -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if ($r.value.Count -ne 1) { throw "expected 1 result, got $($r.value.Count)" }
    }

    Test-Check '$expand surfaces a navigation property' {
        $r = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/users/${userId}?`$expand=manager" -Headers $headers -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.manager) { throw 'expected a manager property to be present' }
    }

    Test-Check 'POST .../oauth2/token (EntraTokenMockPlugin) issues a token' {
        $tokenBody = @{ client_id = 'test'; grant_type = 'client_credentials'; client_secret = 'test'; scope = 'https://graph.microsoft.com/.default' }
        $r = Invoke-RestMethod -Method POST -Uri 'https://login.microsoftonline.com/common/oauth2/token' -Body $tokenBody -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.access_token) { throw 'expected an access_token in the response' }
    }

    # The RemoteCertificateNameMismatch this used to hit unconditionally was
    # traced to a real dev-proxy behavior (installCert:false assigns the root
    # CA itself as a single "generic" certificate for every connection,
    # instead of generating a proper per-domain leaf cert) and fixed by
    # patching ProxyEngine.cs in Build-DevProxyPackage.ps1 - confirmed via a
    # raw TLS handshake showing a correct per-host leaf cert afterward, and
    # via this exact check passing end-to-end (including through the
    # Microsoft Graph SDK's own HTTP client, not just Invoke-RestMethod).
    # Still gated on CertificateTrusted, though: that's a separate, still
    # best-effort concern (mainly on Windows, where the OS-trust attempt
    # itself - not certificate generation - can time out non-interactively).
    if ($ciResult.CertificateTrusted) {
        Test-Check '-CI: fully transparent call succeeds (no -SkipCertificateCheck)' {
            $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers -TimeoutSec 15
            if ($r.value.Count -lt 1) { throw "expected at least 1 user, got $($r.value.Count)" }
        }
    } else {
        Write-Host '  Skipping the fully-transparent-call check: certificate trust did not succeed in this run.' -ForegroundColor Yellow
    }
} finally {
    # Dev Proxy's own console output - the only way to see why it might have
    # failed to bind its ports in the first place, since Start-Process is
    # fire-and-forget and swallows that otherwise (see Start-MsGraphProxy's
    # help). Dumped unconditionally, not just on failure, so a passing run
    # still shows what a clean startup looks like for comparison.
    foreach ($log in @($devProxyStdOutLog, $devProxyStdErrLog)) {
        if (Test-Path -Path $log) {
            Write-Host "`n--- $log ---"
            Get-Content -Path $log | Write-Host
        }
    }

    Write-Host 'Stopping Dev Proxy'
    Stop-MsGraphProxy -Confirm:$false | Out-Null
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "$($failures.Count) smoke test check(s) failed against the packaged build."
}

Write-Host "`nAll smoke tests passed." -ForegroundColor Green
