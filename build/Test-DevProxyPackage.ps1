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

Write-Host "Installing $PackagePath as the $rid build"
if (Test-Path -Path $ridRoot) {
    Remove-Item -Path $ridRoot -Recurse -Force
}
New-Item -Path $ridRoot -ItemType Directory -Force | Out-Null
Expand-Archive -Path $PackagePath -DestinationPath $ridRoot -Force
if ($IsLinux) {
    & chmod +x (Join-Path -Path $ridRoot -ChildPath 'devproxy')
}

# With installCert (Dev Proxy's default) true, Dev Proxy awaits trusting its
# root CA into the OS store *before* it starts listening on its proxy port -
# see ProxyEngine.ExecuteAsync in the dev-proxy source: AddEndPoint (which
# binds the actual proxy listener) only runs after EnsureRootCertificateAsync
# completes. In a non-interactive session that trust step never resolves (see
# the cert-store notes further down), so the listener never binds at all -
# the control API still responds fine since it's a separate host, but the
# proxy port refuses every connection. This script already skips certificate
# validation on its own requests, so it doesn't need Dev Proxy to have
# trusted its cert - only to have started - hence installCert: false here,
# via a copy of the bundled config rather than changing the real one, so real
# end users still get the default (correct, if interactive) trust behavior.
$originalConfigFile = & $module { $script:MsGraphProxyDefaultConfigFile }
$configRoot = Split-Path -Path $originalConfigFile -Parent
$testConfig = Get-Content -Raw -Path $originalConfigFile | ConvertFrom-Json -AsHashtable
$testConfig['installCert'] = $false
$testConfig['mockResponsePlugin']['mocksFile'] = Join-Path -Path $configRoot -ChildPath 'mocks.json'
$testConfig['graphSchemaMockPlugin']['schemaFilePath'] = Join-Path -Path $configRoot -ChildPath 'graph-schema\v1.0.csdl'
$testConfigFile = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'msgraphproxy-test-devproxyrc.json'
$testConfig | ConvertTo-Json -Depth 10 | Set-Content -Path $testConfigFile

# The bundled devproxyrc.json doesn't override Dev Proxy's default proxy port
# (8000 - distinct from the 8897 control-API port), so it's fixed here too.
#
# Test requests are pointed at this proxy explicitly via -Proxy, rather than
# relying on Dev Proxy's system-wide proxy registration to propagate to this
# process the way it does in normal interactive use - that registration is
# Dev Proxy's own well-tested behavior, not something this script needs to
# re-verify, and depending on it here made an earlier CI run's requests
# silently reach the real graph.microsoft.com instead of the local mock.
$proxyUri = 'http://127.0.0.1:8000'

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

# Dev Proxy needs a moment after Start-Process to bind its control API and,
# separately, to finish generating/trusting its root CA, so these get short
# retry loops rather than fixed sleeps, to avoid CI flakiness. The last caught
# exception is surfaced in the final throw - silently retrying without keeping
# it would leave a timeout with no clue *why* it never became ready.
function Wait-ForControlApi {
    param (
        [int]
        $ApiPort,

        [int]
        $TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/proxy" -TimeoutSec 5 | Out-Null
            return
        } catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }
    }

    throw "Dev Proxy's control API (plain HTTP, no TLS involved) did not respond within $TimeoutSeconds seconds - the process likely failed to start at all. Last error: $lastError"
}

# Import-Certificate, certutil -addstore, and raw X509Store.Add() were all
# tried here for trusting Dev Proxy's root CA into CurrentUser\Root, and all
# three hit the same wall: Windows shows an interactive "install this root
# certificate?" confirmation dialog for the Root store specifically,
# regardless of which API triggers it - unusable non-interactively in CI, and
# not something worth fighting for a smoke test. Skipping certificate
# validation on these specific test requests sidesteps it entirely without
# touching how Dev Proxy trusts its own CA for real end users.
function Wait-ProxyReady {
    param (
        [hashtable]
        $Headers,

        [string]
        $ProxyUri,

        [int]
        $TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $Headers -Proxy $ProxyUri -SkipCertificateCheck -TimeoutSec 5 | Out-Null
            return
        } catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }
    }

    throw "Dev Proxy did not become ready to serve HTTPS requests within $TimeoutSeconds seconds. Last error: $lastError"
}

try {
    Write-Host 'Starting Dev Proxy'
    Start-MsGraphProxy -ConfigFile $testConfigFile -Confirm:$false | Out-Null

    $status = Get-MsGraphProxyStatus
    if (-not $status.Running) {
        throw 'Dev Proxy did not report as running after Start-MsGraphProxy.'
    }

    Wait-ForControlApi -ApiPort $status.ApiPort

    $headers = @{ Authorization = 'Bearer faketoken' }
    Wait-ProxyReady -Headers $headers -ProxyUri $proxyUri

    Test-Check 'GET /users returns a mocked collection' {
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if ($r.value.Count -lt 1) { throw "expected at least 1 user, got $($r.value.Count)" }
    }

    $userId = (Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15).value[0].id

    Test-Check 'GET /users/{id} returns the matching item' {
        $r = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/users/$userId" -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if ($r.id -ne $userId) { throw "expected id $userId, got $($r.id)" }
    }

    Test-Check 'POST .../members/$ref adds a reference (204)' {
        $body = @{ '@odata.id' = 'https://graph.microsoft.com/v1.0/directoryObjects/11111111-1111-1111-1111-111111111111' } | ConvertTo-Json
        $r = Invoke-WebRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/groups/22222222-2222-2222-2222-222222222222/members/`$ref" -Headers $headers -Body $body -ContentType 'application/json' -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if ($r.StatusCode -ne 204) { throw "expected 204, got $($r.StatusCode)" }
    }

    Test-Check 'POST .../checkMemberGroups (bound Action) returns a value array' {
        $body = @{ groupIds = @('33333333-3333-3333-3333-333333333333') } | ConvertTo-Json
        $r = Invoke-RestMethod -Method POST -Uri "https://graph.microsoft.com/v1.0/users/$userId/checkMemberGroups" -Headers $headers -Body $body -ContentType 'application/json' -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.value) { throw 'expected a value array in the response' }
    }

    Test-Check 'GET /applications/delta (bound Function) resolves' {
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/applications/delta' -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.value) { throw 'expected a value array in the response' }
    }

    Test-Check 'GET .../mail/$value returns raw text' {
        $r = Invoke-WebRequest -Uri "https://graph.microsoft.com/v1.0/users/$userId/mail/`$value" -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if ($r.Headers['Content-Type'] -notlike 'text/plain*') { throw "expected text/plain, got $($r.Headers['Content-Type'])" }
    }

    Test-Check 'GET /users/$count returns a bare integer' {
        $r = Invoke-WebRequest -Uri 'https://graph.microsoft.com/v1.0/users/$count' -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if ($r.Content -notmatch '^\d+$') { throw "expected a bare integer, got '$($r.Content)'" }
    }

    Test-Check '$filter narrows results' {
        $r = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/users?`$filter=givenName eq 'Jane'" -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if ($r.value.Count -ne 1) { throw "expected 1 result, got $($r.value.Count)" }
    }

    Test-Check '$expand surfaces a navigation property' {
        $r = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/users/${userId}?`$expand=manager" -Headers $headers -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.manager) { throw 'expected a manager property to be present' }
    }

    Test-Check 'POST .../oauth2/token (EntraTokenMockPlugin) issues a token' {
        $tokenBody = @{ client_id = 'test'; grant_type = 'client_credentials'; client_secret = 'test'; scope = 'https://graph.microsoft.com/.default' }
        $r = Invoke-RestMethod -Method POST -Uri 'https://login.microsoftonline.com/common/oauth2/token' -Body $tokenBody -Proxy $proxyUri -SkipCertificateCheck -TimeoutSec 15
        if (-not $r.access_token) { throw 'expected an access_token in the response' }
    }
} finally {
    Write-Host 'Stopping Dev Proxy'
    Stop-MsGraphProxy -Confirm:$false | Out-Null
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "$($failures.Count) smoke test check(s) failed against the packaged build."
}

Write-Host "`nAll smoke tests passed." -ForegroundColor Green
