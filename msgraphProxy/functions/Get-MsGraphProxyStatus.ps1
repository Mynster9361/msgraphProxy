function Get-MsGraphProxyStatus {
	<#
	.SYNOPSIS
		Reports whether the Dev Proxy process started by Start-MsGraphProxy is
		still running.
	
	.DESCRIPTION
		Reads the state file written by Start-MsGraphProxy and checks whether the
		process it recorded is still alive, returning its tracked configuration
		file, executable path, control-API port, recording state and start time
		alongside the current running state.
	
	.EXAMPLE
		PS C:\> Get-MsGraphProxyStatus
	
		Returns an object describing whether Dev Proxy is currently running.
	#>
	[CmdletBinding()]
	param ()

	if (-not (Test-Path -Path $script:MsGraphProxyStateFile)) {
		return [pscustomobject]@{
			Running    = $false
			Id         = $null
			ConfigFile = $null
			ExePath    = $null
			ApiPort    = $null
			Recording  = $false
			StartedAt  = $null
		}
	}

	$state = Get-Content -Path $script:MsGraphProxyStateFile -Raw | ConvertFrom-Json
	$process = Get-Process -Id $state.Id -ErrorAction SilentlyContinue

	[pscustomobject]@{
		Running    = [bool]$process
		Id         = $state.Id
		ConfigFile = $state.ConfigFile
		ExePath    = $state.ExePath
		ApiPort    = $state.ApiPort
		Recording  = [bool]$state.Recording
		StartedAt  = $state.StartedAt
	}
}
