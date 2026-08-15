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

    # /users/$count is a directory-object "advanced query" - real Graph
    # requires ConsistencyLevel: eventual for it (see aad-advanced-queries),
    # so unlike every other check here it needs its own headers.
    $advancedQueryHeaders = @{ Authorization = 'Bearer faketoken'; ConsistencyLevel = 'eventual' }

    Test-Check 'GET /users/$count returns a bare integer' {
        $r = Invoke-WebRequest -Uri 'https://graph.microsoft.com/v1.0/users/$count' -Headers $advancedQueryHeaders -SkipCertificateCheck -TimeoutSec 15
        if ($r.Content -notmatch '^\d+$') { throw "expected a bare integer, got '$($r.Content)'" }
    }

    Test-Check 'GET /users/$count without ConsistencyLevel is rejected (400)' {
        try {
            $r = Invoke-WebRequest -Uri 'https://graph.microsoft.com/v1.0/users/$count' -Headers $headers -SkipCertificateCheck -TimeoutSec 15
            throw "expected 400, got $($r.StatusCode)"
        } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            if ($_.Exception.Response.StatusCode -ne 400) { throw "expected 400, got $($_.Exception.Response.StatusCode)" }
        }
    }

    # One row per $filter method this plugin implements, run against both
    # v1.0 and beta. Each row drives 4 checks: a malformed/unauthorized
    # request that must be rejected (400), a well-formed one that must
    # succeed (200), a well-formed one with criteria that legitimately
    # matches nothing (200, 0 results), and one that legitimately matches
    # (200, an exact expected count) - proving each operator both rejects
    # what it should and correctly discriminates true from false, not just
    # that it doesn't crash. RequiresAdvancedQuery methods (ne/not/endsWith)
    # reuse that same shape: "malformed" becomes "missing ConsistencyLevel/
    # $count=true", exactly the real rejection reason for those operators on
    # a directory object (see https://learn.microsoft.com/graph/aad-advanced-queries).
    #
    # Values below are chosen against this plugin's own known seed data for
    # microsoft.graph.user (shared identically between v1.0 and beta - the
    # underlying pool is keyed by type, not API version): user 1 has
    # givenName "Test", surname "User", mail/userPrincipalName
    # "testuser@contoso.com", companyName "Contoso", jobTitle "Developer";
    # user 2 (SecondSample) has givenName "Jane", surname "Doe", mail/
    # userPrincipalName "jane.doe@contoso.com", same companyName and
    # jobTitle as user 1.
    $filterMethodTests = @(
        @{ Name = 'eq'; RequiresAdvancedQuery = $false
           Malformed = "givenName eq"
           Valid     = "givenName eq 'Test'"
           NoMatch   = "givenName eq 'Nobody'"
           Match     = "givenName eq 'Jane'"; ExpectedCount = 1 }
        @{ Name = 'ne'; RequiresAdvancedQuery = $true
           Malformed = "jobTitle ne 'Manager'"
           Valid     = "jobTitle ne 'Manager'"
           NoMatch   = "companyName ne 'Contoso'"
           Match     = "givenName ne 'Jane'"; ExpectedCount = 1 }
        @{ Name = 'gt'; RequiresAdvancedQuery = $false
           Malformed = "jobTitle gt"
           Valid     = "officeLocation gt 'A'"
           NoMatch   = "jobTitle gt 'ZZZZZZZZ'"
           Match     = "jobTitle gt 'A'"; ExpectedCount = 2 }
        @{ Name = 'lt'; RequiresAdvancedQuery = $false
           Malformed = "jobTitle lt"
           Valid     = "officeLocation lt 'zzzzzzzz'"
           NoMatch   = "jobTitle lt 'A'"
           Match     = "jobTitle lt 'zzzzzzzz'"; ExpectedCount = 2 }
        @{ Name = 'ge'; RequiresAdvancedQuery = $false
           Malformed = "jobTitle ge"
           Valid     = "officeLocation ge 'A'"
           NoMatch   = "jobTitle ge 'zzzzzzzz'"
           Match     = "jobTitle ge 'Developer'"; ExpectedCount = 2 }
        @{ Name = 'le'; RequiresAdvancedQuery = $false
           Malformed = "jobTitle le"
           Valid     = "officeLocation le 'zzzzzzzz'"
           NoMatch   = "jobTitle le 'A'"
           Match     = "jobTitle le 'Developer'"; ExpectedCount = 2 }
        @{ Name = 'and'; RequiresAdvancedQuery = $false
           Malformed = "givenName eq 'Jane' and"
           Valid     = "givenName eq 'Jane' and surname eq 'Doe'"
           NoMatch   = "givenName eq 'Jane' and surname eq 'User'"
           Match     = "givenName eq 'Jane' and surname eq 'Doe'"; ExpectedCount = 1 }
        @{ Name = 'or'; RequiresAdvancedQuery = $false
           Malformed = "givenName eq 'Jane' or"
           Valid     = "givenName eq 'Jane' or givenName eq 'Test'"
           NoMatch   = "givenName eq 'Nobody' or givenName eq 'NoneEither'"
           Match     = "givenName eq 'Jane' or givenName eq 'Test'"; ExpectedCount = 2 }
        @{ Name = 'not'; RequiresAdvancedQuery = $true
           Malformed = "not(jobTitle eq 'Manager')"
           Valid     = "not(jobTitle eq 'Manager')"
           NoMatch   = "not(companyName eq 'Contoso')"
           Match     = "not(givenName eq 'Jane')"; ExpectedCount = 1 }
        @{ Name = 'startswith'; RequiresAdvancedQuery = $false
           Malformed = "startswith(givenName)"
           Valid     = "startswith(mail,'test')"
           NoMatch   = "startswith(givenName,'Zz')"
           Match     = "startswith(givenName,'Ja')"; ExpectedCount = 1 }
        @{ Name = 'contains'; RequiresAdvancedQuery = $false
           Malformed = "contains(givenName)"
           Valid     = "contains(mail,'contoso')"
           NoMatch   = "contains(givenName,'xyz')"
           Match     = "contains(mail,'jane')"; ExpectedCount = 1 }
        @{ Name = 'endswith'; RequiresAdvancedQuery = $true
           Malformed = "endswith(jobTitle,'er')"
           Valid     = "endswith(jobTitle,'er')"
           NoMatch   = "endswith(mail,'nobody.com')"
           Match     = "endswith(mail,'contoso.com')"; ExpectedCount = 2 }
        @{ Name = 'in'; RequiresAdvancedQuery = $false
           Malformed = "givenName in ("
           Valid     = "givenName in ('Jane','Test')"
           NoMatch   = "givenName in ('Nobody','NoneEither')"
           Match     = "givenName in ('Jane','Test')"; ExpectedCount = 2 }
    )

    foreach ($apiVersion in @('v1.0', 'beta')) {
        foreach ($t in $filterMethodTests) {
            $countSuffix = if ($t.RequiresAdvancedQuery) { '&$count=true' } else { '' }
            $wellFormedHeaders = if ($t.RequiresAdvancedQuery) { $advancedQueryHeaders } else { $headers }

            Test-Check "[$apiVersion] `$filter $($t.Name): malformed/unauthorized is rejected (400)" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$filter=$([Uri]::EscapeDataString($t.Malformed))"
                try {
                    $r = Invoke-WebRequest -Uri $uri -Headers $headers -SkipCertificateCheck -TimeoutSec 15
                    throw "expected 400, got $($r.StatusCode)"
                } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                    if ($_.Exception.Response.StatusCode -ne 400) { throw "expected 400, got $($_.Exception.Response.StatusCode)" }
                }
            }

            Test-Check "[$apiVersion] `$filter $($t.Name): well-formed succeeds (200)" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$filter=$([Uri]::EscapeDataString($t.Valid))$countSuffix"
                $r = Invoke-RestMethod -Uri $uri -Headers $wellFormedHeaders -SkipCertificateCheck -TimeoutSec 15
                if ($null -eq $r.value) { throw 'expected a value array in the response' }
            }

            Test-Check "[$apiVersion] `$filter $($t.Name): no-match returns 0 results" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$filter=$([Uri]::EscapeDataString($t.NoMatch))$countSuffix"
                $r = Invoke-RestMethod -Uri $uri -Headers $wellFormedHeaders -SkipCertificateCheck -TimeoutSec 15
                if ($r.value.Count -ne 0) { throw "expected 0 results, got $($r.value.Count)" }
            }

            Test-Check "[$apiVersion] `$filter $($t.Name): match returns $($t.ExpectedCount) result(s)" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$filter=$([Uri]::EscapeDataString($t.Match))$countSuffix"
                $r = Invoke-RestMethod -Uri $uri -Headers $wellFormedHeaders -SkipCertificateCheck -TimeoutSec 15
                if ($r.value.Count -ne $t.ExpectedCount) { throw "expected $($t.ExpectedCount) result(s), got $($r.value.Count)" }
            }
        }
    }

    # $search doesn't decompose into per-operator methods the way $filter
    # does - it's always gated on directory objects (ConsistencyLevel only,
    # no $count=true needed - the one documented exception to the general
    # advanced-query rule), so every row's "Malformed" case is the missing-
    # header rejection. Same seed data as the $filter table above.
    $searchScenarioTests = @(
        @{ Name = 'clause (tokenized displayName match)'
           Malformed = '"displayName:Jane"'
           Valid     = '"displayName:Jane"'
           NoMatch   = '"displayName:Nobody"'
           Match     = '"displayName:Jane"'; ExpectedCount = 1 }
        @{ Name = 'clause (token order-independence)'
           Malformed = '"displayName:Doe Jane"'
           Valid     = '"displayName:Doe Jane"'
           NoMatch   = '"displayName:Nobody Whoever"'
           Match     = '"displayName:Doe Jane"'; ExpectedCount = 1 }
        @{ Name = 'AND'
           Malformed = '"displayName:Jane" AND "surname:Doe"'
           Valid     = '"displayName:Jane" AND "surname:Doe"'
           NoMatch   = '"displayName:Jane" AND "displayName:User"'
           Match     = '"displayName:Jane" AND "surname:Doe"'; ExpectedCount = 1 }
        @{ Name = 'OR'
           Malformed = '"displayName:Jane" OR "displayName:Test"'
           Valid     = '"displayName:Jane" OR "displayName:Test"'
           NoMatch   = '"displayName:Nobody" OR "displayName:NoneEither"'
           Match     = '"displayName:Jane" OR "displayName:Test"'; ExpectedCount = 1 }
        @{ Name = 'non-displayName property falls back to startswith'
           Malformed = '"mail:jane"'
           Valid     = '"mail:jane"'
           NoMatch   = '"mail:zzz"'
           Match     = '"mail:jane"'; ExpectedCount = 1 }
    )

    foreach ($apiVersion in @('v1.0', 'beta')) {
        foreach ($t in $searchScenarioTests) {
            Test-Check "[$apiVersion] `$search $($t.Name): missing ConsistencyLevel is rejected (400)" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$search=$([Uri]::EscapeDataString($t.Malformed))"
                try {
                    $r = Invoke-WebRequest -Uri $uri -Headers $headers -SkipCertificateCheck -TimeoutSec 15
                    throw "expected 400, got $($r.StatusCode)"
                } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                    if ($_.Exception.Response.StatusCode -ne 400) { throw "expected 400, got $($_.Exception.Response.StatusCode)" }
                }
            }

            Test-Check "[$apiVersion] `$search $($t.Name): well-formed succeeds (200)" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$search=$([Uri]::EscapeDataString($t.Valid))"
                $r = Invoke-RestMethod -Uri $uri -Headers $advancedQueryHeaders -SkipCertificateCheck -TimeoutSec 15
                if ($null -eq $r.value) { throw 'expected a value array in the response' }
            }

            Test-Check "[$apiVersion] `$search $($t.Name): no-match returns 0 results" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$search=$([Uri]::EscapeDataString($t.NoMatch))"
                $r = Invoke-RestMethod -Uri $uri -Headers $advancedQueryHeaders -SkipCertificateCheck -TimeoutSec 15
                if ($r.value.Count -ne 0) { throw "expected 0 results, got $($r.value.Count)" }
            }

            Test-Check "[$apiVersion] `$search $($t.Name): match returns $($t.ExpectedCount) result(s)" {
                $uri = "https://graph.microsoft.com/$apiVersion/users?`$search=$([Uri]::EscapeDataString($t.Match))"
                $r = Invoke-RestMethod -Uri $uri -Headers $advancedQueryHeaders -SkipCertificateCheck -TimeoutSec 15
                if ($r.value.Count -ne $t.ExpectedCount) { throw "expected $($t.ExpectedCount) result(s), got $($r.value.Count)" }
            }
        }

        # Distinct from the table above: these are rejected for bad grammar,
        # not a missing header - ConsistencyLevel IS present on both, so a
        # 400 here can only be the parser correctly refusing the input.
        Test-Check "[$apiVersion] `$search: lowercase 'and' is rejected (400)" {
            $uri = "https://graph.microsoft.com/$apiVersion/users?`$search=$([Uri]::EscapeDataString('"displayName:Jane" and "surname:Doe"'))"
            try {
                $r = Invoke-WebRequest -Uri $uri -Headers $advancedQueryHeaders -SkipCertificateCheck -TimeoutSec 15
                throw "expected 400, got $($r.StatusCode)"
            } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                if ($_.Exception.Response.StatusCode -ne 400) { throw "expected 400, got $($_.Exception.Response.StatusCode)" }
            }
        }

        Test-Check "[$apiVersion] `$search: unterminated clause is rejected (400)" {
            $uri = "https://graph.microsoft.com/$apiVersion/users?`$search=$([Uri]::EscapeDataString('"displayName:Jane'))"
            try {
                $r = Invoke-WebRequest -Uri $uri -Headers $advancedQueryHeaders -SkipCertificateCheck -TimeoutSec 15
                throw "expected 400, got $($r.StatusCode)"
            } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                if ($_.Exception.Response.StatusCode -ne 400) { throw "expected 400, got $($_.Exception.Response.StatusCode)" }
            }
        }
    }

    # https://learn.microsoft.com/graph/json-batching. GraphMockResponsePlugin
    # (dev-proxy's own built-in mocks.json plugin, not ours) used to swallow
    # every $batch request outright whenever mocks.json had no matching
    # entries - unlike its own non-batch behavior, which correctly falls
    # through - so these checks also stand as a regression test for that
    # source patch in Build-DevProxyPackage.ps1, not just for
    # GraphSchemaMockPlugin's own $batch support.
    Test-Check '$batch: unrelated sub-requests each resolve independently' {
        $body = @{
            requests = @(
                @{ id = '1'; method = 'GET'; url = '/users' }
                @{ id = '2'; method = 'GET'; url = "/users/$userId" }
                @{ id = '3'; method = 'POST'; url = "/groups/22222222-2222-2222-2222-222222222222/members/`$ref"; body = @{ '@odata.id' = 'https://graph.microsoft.com/v1.0/directoryObjects/11111111-1111-1111-1111-111111111111' }; headers = @{ 'Content-Type' = 'application/json' } }
            )
        } | ConvertTo-Json -Depth 10
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/$batch' -Method POST -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        $byId = @{}
        foreach ($resp in $r.responses) { $byId[$resp.id] = $resp.status }
        if ($byId['1'] -ne 200) { throw "expected id 1 status 200, got $($byId['1'])" }
        if ($byId['2'] -ne 200) { throw "expected id 2 status 200, got $($byId['2'])" }
        if ($byId['3'] -ne 204) { throw "expected id 3 status 204, got $($byId['3'])" }
    }

    Test-Check '$batch: dependsOn sequencing (doc''s own 1->2->4->3 example)' {
        $body = @{
            requests = @(
                @{ id = '1'; method = 'GET'; url = '/users' }
                @{ id = '2'; dependsOn = @('1'); method = 'GET'; url = '/users' }
                @{ id = '4'; dependsOn = @('2'); method = 'GET'; url = '/users' }
                @{ id = '3'; dependsOn = @('4'); method = 'GET'; url = '/users' }
            )
        } | ConvertTo-Json -Depth 10
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/$batch' -Method POST -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        if (($r.responses | Where-Object { $_.status -ne 200 }).Count -ne 0) { throw "expected all 4 sub-responses to be 200, got: $($r.responses | ConvertTo-Json -Compress)" }
    }

    Test-Check '$batch: a failed dependency propagates 424 to its dependent' {
        $body = @{
            requests = @(
                @{ id = '1'; method = 'GET'; url = "/users?`$filter=givenName eq" }
                @{ id = '2'; dependsOn = @('1'); method = 'GET'; url = '/users' }
            )
        } | ConvertTo-Json -Depth 10
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/$batch' -Method POST -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        $byId = @{}
        foreach ($resp in $r.responses) { $byId[$resp.id] = $resp.status }
        if ($byId['1'] -ne 400) { throw "expected id 1 (malformed `$filter) status 400, got $($byId['1'])" }
        if ($byId['2'] -ne 424) { throw "expected id 2 (depends on failed id 1) status 424, got $($byId['2'])" }
    }

    Test-Check '$batch: per-sub-request headers are independent (ConsistencyLevel on one, not the other)' {
        $body = @{
            requests = @(
                @{ id = '1'; method = 'GET'; url = "users?`$filter=givenName ne 'Jane'&`$count=true"; headers = @{ ConsistencyLevel = 'eventual' } }
                @{ id = '2'; method = 'GET'; url = "users?`$filter=givenName ne 'Jane'" }
            )
        } | ConvertTo-Json -Depth 10
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/$batch' -Method POST -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        $byId = @{}
        foreach ($resp in $r.responses) { $byId[$resp.id] = $resp.status }
        if ($byId['1'] -ne 200) { throw "expected id 1 (has ConsistencyLevel) status 200, got $($byId['1'])" }
        if ($byId['2'] -ne 400) { throw "expected id 2 (missing ConsistencyLevel) status 400, got $($byId['2'])" }
    }

    Test-Check '$batch: duplicate request id is rejected (400)' {
        $body = @{ requests = @(@{ id = '1'; method = 'GET'; url = '/users' }, @{ id = '1'; method = 'GET'; url = '/users' }) } | ConvertTo-Json -Depth 10
        try {
            $r = Invoke-WebRequest -Uri 'https://graph.microsoft.com/v1.0/$batch' -Method POST -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
            throw "expected 400, got $($r.StatusCode)"
        } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            if ($_.Exception.Response.StatusCode -ne 400) { throw "expected 400, got $($_.Exception.Response.StatusCode)" }
        }
    }

    Test-Check '$batch: an unresolvable sub-request path gets its own 404, outer batch stays 200' {
        $body = @{ requests = @(@{ id = '1'; method = 'GET'; url = '/totally/not/a/real/graph/path' }) } | ConvertTo-Json -Depth 10
        $r = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/$batch' -Method POST -Headers $headers -Body $body -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
        if ($r.responses[0].status -ne 404) { throw "expected 404, got $($r.responses[0].status)" }
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
