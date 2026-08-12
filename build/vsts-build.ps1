<#
This script wraps up the module, creating a finished artifact, ready to publish a repository such as the PS Gallery.
Useres PowerShellGet for interaction with the package management system.

Insert any build steps you may need to take before publishing it here.
#>
[CmdletBinding()]
param (
	[string]
	$WorkingDirectory,
	
	[string]
	$Repository = 'PSGallery',
	
	[switch]
	$AutoVersion,

	[switch]
	$ExportFunctions
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

#region Handle Configuration
$config = Import-PowerShellDataFile -Path (Join-Path -Path $WorkingDirectory -ChildPath 'config.psd1') -ErrorAction Stop
if ($PSBoundParameters.Keys -notcontains 'AutoVersion') {
	$AutoVersion = $config.AutoVersion
}
if ($PSBoundParameters.Keys -notcontains 'ExportFunctions') {
	$ExportFunctions = $config.ExportFunctions
}
#endregion Handle Configuration

# Prepare publish folder
Write-Host "Creating and populating publishing directory"
$publishDir = New-Item -Path $WorkingDirectory -Name publish -ItemType Directory -Force
Remove-Item -Path "$publishDir/*" -Recurse -Force
Copy-Item -Path "$($WorkingDirectory)\msgraphProxy" -Destination $publishDir.FullName -Recurse -Force

#region Gather text data to compile
$text = @('$script:ModuleRoot = $PSScriptRoot')

# Gather commands
Get-ChildItem -Path "$($publishDir.FullName)\msgraphProxy\internal\functions\" -Recurse -File -Filter "*.ps1" | ForEach-Object {
	$text += [System.IO.File]::ReadAllText($_.FullName)
}
Get-ChildItem -Path "$($publishDir.FullName)\msgraphProxy\functions\" -Recurse -File -Filter "*.ps1" | ForEach-Object {
	$text += [System.IO.File]::ReadAllText($_.FullName)
}

# Gather scripts
Get-ChildItem -Path "$($publishDir.FullName)\msgraphProxy\internal\scripts\" -Recurse -File -Filter "*.ps1" | ForEach-Object {
	$text += [System.IO.File]::ReadAllText($_.FullName)
}

# Add Explicit Export Statement (to avoid direct invocation of the .psm1 file giving access to non-exported functions)
$functionNames = (Get-ChildItem -Path "$($WorkingDirectory)\msgraphProxy\functions" -Filter '*.ps1' -Recurse).BaseName | Sort-Object
if ($functionNames) {
	$text += "Export-ModuleMember -Function '$($functionNames -join "','")'"
}

#region Update the psm1 file & Cleanup
[System.IO.File]::WriteAllText("$($publishDir.FullName)\msgraphProxy\msgraphProxy.psm1", ($text -join "`n`n"), [System.Text.Encoding]::UTF8)
Remove-Item -Path "$($publishDir.FullName)\msgraphProxy\internal" -Recurse -Force
Remove-Item -Path "$($publishDir.FullName)\msgraphProxy\functions" -Recurse -Force
#endregion Update the psm1 file & Cleanup

#region Updating the Module Version
if ($AutoVersion) {
	Write-Host "Updating module version numbers."
	try { [version]$remoteVersion = (Find-Module 'msgraphProxy' -Repository $Repository -ErrorAction Stop).Version }
	catch {
		throw "Failed to access $($Repository) : $_"
	}
	if (-not $remoteVersion) {
		throw "Couldn't find msgraphProxy on repository $($Repository) : $_"
	}
	$newBuildNumber = $remoteVersion.Build + 1
	[version]$localVersion = (Import-PowerShellDataFile -Path "$($publishDir.FullName)\msgraphProxy\msgraphProxy.psd1").ModuleVersion
	Update-ModuleManifest -Path "$($publishDir.FullName)\msgraphProxy\msgraphProxy.psd1" -ModuleVersion "$($localVersion.Major).$($localVersion.Minor).$($newBuildNumber)"
}
#endregion Updating the Module Version

#region Export Functions
if ($ExportFunctions) {
	Write-Host "Exporting all public functions"

	$functionFiles = Get-ChildItem -Path "$($WorkingDirectory)\msgraphProxy\functions" -Filter '*.ps1' -Recurse
	Update-ModuleManifest -Path "$($publishDir.FullName)\msgraphProxy\msgraphProxy.psd1" -FunctionsToExport ($functionFiles.BaseName | Sort-Object)
}
#endregion Export Functions