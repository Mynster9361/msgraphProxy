function Stop-MsGraphProxy {
	<#
	.SYNOPSIS
		Stops the Dev Proxy process started by Start-MsGraphProxy.
	
	.DESCRIPTION
		If Dev Proxy is recording, first stops the recording through its control
		API. That triggers its reporting plugins (Graph minimal permissions,
		execution summary) to analyze what was recorded; their results are
		collected and returned as part of the result object under Recording.
	
		Then asks Dev Proxy to shut down gracefully through its control API, so
		it can unregister itself as the Windows system proxy on its way out.
		Only if that doesn't succeed within the timeout does it force-kill the
		process - and in that case it also clears the Windows system-proxy
		registration itself, since a force-kill skips the cleanup Dev Proxy
		would otherwise have done.
	
	.PARAMETER TimeoutSeconds
		How long to wait for a graceful shutdown before falling back to killing
		the process outright.
	
	.PARAMETER WhatIf
		If this switch is enabled, no actions are performed but informational
		messages will be displayed that explain what would happen if the command
		were to run.
	
	.PARAMETER Confirm
		If this switch is enabled, you will be prompted for confirmation before
		executing any operations that change state.
	
	.EXAMPLE
		PS C:\> Stop-MsGraphProxy
	
		Stops the running Dev Proxy instance and returns any recorded reports.
	#>
	[CmdletBinding(SupportsShouldProcess)]
	param (
		[int]
		$TimeoutSeconds = 10
	)

	$status = Get-MsGraphProxyStatus
	if (-not $status.Running) {
		Write-Warning 'Dev Proxy is not running.'
		Remove-Item -Path $script:MsGraphProxyStateFile -Force -ErrorAction SilentlyContinue
		Clear-MsGraphProxySystemProxy
		return
	}

	if (-not $PSCmdlet.ShouldProcess("Dev Proxy (PID $($status.Id))", 'Stop')) {
		return
	}

	$apiPort = $status.ApiPort
	if (-not $apiPort) {
		$apiPort = $script:MsGraphProxyDefaultApiPort
	}

	$recording = $null
	if ($status.Recording -and $status.ExePath) {
		$recording = Receive-MsGraphProxyRecording -ApiPort $apiPort -WorkingDirectory (Split-Path -Path $status.ExePath -Parent)
	}

	$stoppedGracefully = $false
	try {
		Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$apiPort/proxy/stopProxy" -TimeoutSec 5 | Out-Null

		$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
		while ((Get-Date) -lt $deadline) {
			if (-not (Get-Process -Id $status.Id -ErrorAction SilentlyContinue)) {
				$stoppedGracefully = $true
				break
			}
			Start-Sleep -Milliseconds 250
		}
	} catch {
		Write-Verbose "Graceful stop via the API failed: $_"
	}

	if (-not $stoppedGracefully) {
		Write-Warning "Dev Proxy didn't stop gracefully via its API; forcing termination and clearing the Windows system proxy."
		Stop-Process -Id $status.Id -Force -ErrorAction SilentlyContinue
		Clear-MsGraphProxySystemProxy
	}

	Remove-Item -Path $script:MsGraphProxyStateFile -Force -ErrorAction SilentlyContinue
	Write-Verbose "Dev Proxy (PID $($status.Id)) stopped."

	[pscustomobject]@{
		Id        = $status.Id
		StoppedAt = (Get-Date).ToString('o')
		Recording = $recording
	}
}
