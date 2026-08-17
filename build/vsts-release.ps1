[CmdletBinding()]
param (
	[string]
	$WorkingDirectory
)

#region Handle Working Directory Defaults
if (-not $WorkingDirectory) {
	if ($env:RELEASE_PRIMARYARTIFACTSOURCEALIAS) {
		$WorkingDirectory = Join-Path -Path $env:SYSTEM_DEFAULTWORKINGDIRECTORY -ChildPath $env:RELEASE_PRIMARYARTIFACTSOURCEALIAS
	}
	else { $WorkingDirectory = $env:SYSTEM_DEFAULTWORKINGDIRECTORY }
}
if (-not $WorkingDirectory) { $WorkingDirectory = Split-Path $PSScriptRoot }
#endregion Handle Working Directory Defaults

if (-not (Test-Path -Path "$WorkingDirectory\publish\msgraphProxy")) {
	throw "Failed to create release: Cannot find the built code of the module! Run the build step first on the same agent!"
}

$config = Import-PowerShellDataFile -Path (Join-Path -Path $WorkingDirectory -ChildPath 'config.psd1') -ErrorAction Stop
if (-not $config.GithubRelease) {
	Write-Host "Skipping the Github release as configured"
	return
}

$moduleVersion = (Import-PowerShellDataFile -Path "$WorkingDirectory\publish\msgraphProxy\msgraphProxy.psd1").ModuleVersion

# Step 1: Zip Module Content
Write-Host "Wrapping up built module into a zip archive"
Compress-Archive -Path "$WorkingDirectory\publish\msgraphProxy\*" -DestinationPath "$WorkingDirectory\publish\msgraphProxy.zip" -Force

# Step 2: Pull in the most recent Dev Proxy binaries build
#
# This release is the one meant to be GitHub's "latest" (see make_latest
# below), and Install-MsGraphProxy reads /releases/latest expecting to find
# Dev Proxy's per-RID zips there - so this release needs to actually carry
# them, not just the module content. build-devproxy-binaries.yml publishes
# its own builds as prereleases (tagged devproxy-*) specifically so they
# never compete for "latest" and can just be pulled in here instead.
#
# Race condition, confirmed happening for real: build-devproxy-binaries.yml
# triggers on the SAME push as this workflow, but as an independently
# scheduled run - nothing orders them relative to each other. A plugins-src
# fix got bundled into the very next module release using the *previous*
# devproxy-* prerelease, because this script reached this step while
# build-devproxy-binaries.yml was still mid-build (cloning dev-proxy,
# publishing 3 RIDs, testing on 3 OSes) for the same commit; the fix itself
# published fine a few minutes later, but nothing ever re-bundled it into a
# release /releases/latest would actually serve - Install-MsGraphProxy never
# saw it.
#
# build-devproxy-binaries.yml is path-filtered (build/plugins-src/**,
# build/Build-DevProxyPackage.ps1, build/Test-DevProxyPackage.ps1) - most
# pushes to main don't touch any of that and never trigger it at all, so
# this can't just poll "until a run shows up or N minutes pass": for an
# ordinary PS-only release that would mean waiting out the full timeout
# every single time for a run that was never coming. Instead, the push
# event payload itself says exactly which files this commit touched, so
# whether to wait at all is known up front, with no guessing from timing.
function Test-TouchesDevProxySource {
	param([string[]]$ChangedPaths)

	foreach ($path in $ChangedPaths) {
		if ($path -like 'build/plugins-src/*' -or
			$path -eq 'build/Build-DevProxyPackage.ps1' -or
			$path -eq 'build/Test-DevProxyPackage.ps1') {
			return $true
		}
	}

	return $false
}

$headSha = $env:GITHUB_SHA
$touchesDevProxySource = $false
if ($env:GITHUB_EVENT_PATH -and (Test-Path -Path $env:GITHUB_EVENT_PATH)) {
	$pushEvent = Get-Content -Raw -Path $env:GITHUB_EVENT_PATH | ConvertFrom-Json
	$changedPaths = @() + $pushEvent.head_commit.added + $pushEvent.head_commit.modified + $pushEvent.head_commit.removed
	$touchesDevProxySource = Test-TouchesDevProxySource -ChangedPaths $changedPaths
}

if ($touchesDevProxySource -and $headSha) {
	Write-Host "This commit touches Dev Proxy plugin sources - waiting for build-devproxy-binaries.yml to finish for $headSha"
	$deadline = (Get-Date).AddMinutes(25)
	$matchingRun = $null
	do {
		$runs = gh run list --repo Mynster9361/msgraphProxy --workflow build-devproxy-binaries.yml --json headSha,status,conclusion --limit 20 | ConvertFrom-Json
		$matchingRun = $runs | Where-Object headSha -eq $headSha | Select-Object -First 1

		if ($matchingRun -and $matchingRun.status -eq 'completed') {
			break
		}

		Write-Host $(if ($matchingRun) { "build-devproxy-binaries.yml is still running for this commit ($($matchingRun.status)) - waiting..." } else { "No build-devproxy-binaries.yml run visible yet for this commit - waiting for it to start..." })
		Start-Sleep -Seconds 15
	} while ((Get-Date) -lt $deadline)

	if (-not $matchingRun -or $matchingRun.status -ne 'completed') {
		throw "Timed out waiting for build-devproxy-binaries.yml to finish for $headSha - refusing to bundle a devproxy-* build that might not include this commit's changes."
	}
	if ($matchingRun.conclusion -ne 'success') {
		throw "build-devproxy-binaries.yml for $headSha finished with conclusion '$($matchingRun.conclusion)' - refusing to bundle stale or failed Dev Proxy binaries."
	}
}

Write-Host "Downloading the most recent Dev Proxy binaries build"
$devProxyReleaseTag = gh release list --repo Mynster9361/msgraphProxy --limit 20 --json tagName,isDraft --jq '[.[] | select(.isDraft == false) | select(.tagName | startswith("devproxy-"))][0].tagName'
if (-not $devProxyReleaseTag) {
	throw "No devproxy-* release found to bundle into this release - has build-devproxy-binaries.yml run at least once?"
}

$devProxyAssetsDir = Join-Path -Path $WorkingDirectory -ChildPath 'publish\devproxy-binaries'
New-Item -Path $devProxyAssetsDir -ItemType Directory -Force | Out-Null
gh release download $devProxyReleaseTag --repo Mynster9361/msgraphProxy --pattern '*.zip' --dir $devProxyAssetsDir --clobber

# Step 3: Create Release
Write-Host "Registering new release for version $($moduleVersion) with Github"
$response = Invoke-RestMethod -Method POST -Uri 'https://api.github.com/repos/Mynster9361/msgraphProxy/releases' -Headers @{
	Authorization = "Bearer $env:GH_TOKEN"
	Accept = 'application/vnd.github+json'
	'X-GitHub-Api-Version' = '2022-11-28'
} -Body (@{
	tag_name = "v$moduleVersion"
	name = "v$moduleVersion"
	body = "Releasing v$moduleVersion of the msgraphProxy module, bundled with Dev Proxy binaries from $devProxyReleaseTag."
	make_latest = 'true'
} | ConvertTo-Json -Depth 10 -Compress)

# Step 4: Upload the module zip and the Dev Proxy binary zips as release assets
$assetFiles = @((Get-Item -Path "$WorkingDirectory\publish\msgraphProxy.zip")) + @(Get-ChildItem -Path $devProxyAssetsDir -Filter *.zip)
foreach ($assetFile in $assetFiles) {
	Write-Host "Publishing $($assetFile.Name) to new release"
	Invoke-RestMethod -Method POST -Uri "$($response.assets_url -replace 'api\.github\.com', 'uploads.github.com')?name=$($assetFile.Name)" -Headers @{
		Authorization = "Bearer $env:GH_TOKEN"
		Accept = 'application/vnd.github+json'
		'X-GitHub-Api-Version' = '2022-11-28'
		'Content-Type' = 'application/octet-stream'
	} -InFile $assetFile.FullName
}
