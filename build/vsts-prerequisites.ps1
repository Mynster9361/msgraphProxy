<#
Uses PowerShellGet to install all modules required to run the pipeline.
#>
[CmdletBinding()]
param (
    [string]
    $Repository = 'PSGallery'
)

$modules = @(
	'Pester' # Testing Framework
	'PSScriptAnalyzer' # Best Practices Analyzer used during tests
	'Microsoft.PowerShell.PlatyPS' # Generate docs from help
	'PSModuleDevelopment' # Potentially used in Tests or Publish
)

# Automatically add missing dependencies
$data = Import-PowerShellDataFile -Path "$PSScriptRoot\..\msgraphProxy\msgraphProxy.psd1"
foreach ($dependency in $data.RequiredModules) {
    if ($dependency -is [string]) {
        if ($modules -contains $dependency) { continue }
        $modules += $dependency
    }
    else {
        if ($modules -contains $dependency.ModuleName) { continue }
        $modules += $dependency.ModuleName
    }
}

foreach ($module in $modules) {
    Write-Host "Installing $module" -ForegroundColor Cyan
    Install-Module $module -Force -SkipPublisherCheck -Repository $Repository
    Import-Module $module -Force -PassThru
}