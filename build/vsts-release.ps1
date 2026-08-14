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
