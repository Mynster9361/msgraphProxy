function Receive-MsGraphProxyRecording {
	<#
	.SYNOPSIS
		Stops Dev Proxy's active recording and collects the resulting reports.
	
	.DESCRIPTION
		Calls Dev Proxy's control API to stop recording, which synchronously
		triggers its reporting plugins (such as GraphMinimalPermissionsPlugin and
		ExecutionSummaryPlugin) to analyze what was recorded. Because JsonReporter
		is enabled in this module's bundled configuration, those plugins write
		their results as JSON files into Dev Proxy's working directory; this
		function reads them, parses them, deletes them, and returns them as a
		single object keyed by report name.
	
	.PARAMETER ApiPort
		Port of Dev Proxy's control API.
	
	.PARAMETER WorkingDirectory
		The directory Dev Proxy was started in, where report files are written.
	
	.EXAMPLE
		PS C:\> Receive-MsGraphProxyRecording -ApiPort 8897 -WorkingDirectory 'C:\bin\win-x64'
	
		Stops recording and returns any reports Dev Proxy generated.
	#>
	[CmdletBinding()]
	param (
		[Parameter(Mandatory)]
		[int]
		$ApiPort,

		[Parameter(Mandatory)]
		[string]
		$WorkingDirectory
	)

	$reportFilter = 'GraphMinimalPermissions*_JsonReporter.json'
	Get-ChildItem -Path $WorkingDirectory -Filter $reportFilter -ErrorAction SilentlyContinue |
		Remove-Item -Force -ErrorAction SilentlyContinue

	try {
		Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$ApiPort/proxy" `
			-ContentType 'application/json' -Body '{"recording":false}' -TimeoutSec 30 | Out-Null
	} catch {
		Write-Verbose "Stopping the recording via the API failed: $_"
		return $null
	}

	$reportFiles = Get-ChildItem -Path $WorkingDirectory -Filter $reportFilter -ErrorAction SilentlyContinue
	if (-not $reportFiles) {
		return $null
	}

	$reports = [ordered]@{}
	foreach ($reportFile in $reportFiles) {
		$reportName = $reportFile.BaseName -replace '_JsonReporter$', ''
		$reports[$reportName] = Get-Content -Path $reportFile.FullName -Raw | ConvertFrom-Json
		#Remove-Item -Path $reportFile.FullName -Force -ErrorAction SilentlyContinue
	}

	[pscustomobject]$reports
}
