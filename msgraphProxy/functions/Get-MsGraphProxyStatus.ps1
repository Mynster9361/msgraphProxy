function Get-MsGraphProxyStatus {
	<#
	.SYNOPSIS
		Reports whether the Dev Proxy process started by Start-MsGraphProxy is
		still running.
	
	.DESCRIPTION
		Reads the state file written by Start-MsGraphProxy and checks whether
		the process it recorded is still alive. Returns an object with:
			Running    - whether the process is currently alive
			Id         - its process ID
			ConfigFile - the devproxyrc.json it was started with
			ExePath    - path to the Dev Proxy executable
			ApiPort    - its control-API port
			Recording  - whether it's currently recording
			StartedAt  - when it was started
		If Start-MsGraphProxy was never called (or Stop-MsGraphProxy already
		cleaned up), all of these are $false/$null.

	.EXAMPLE
		PS C:\> Get-MsGraphProxyStatus

		Returns an object describing whether Dev Proxy is currently running.
	
	.LINK
		https://mynster-it.dk/docs/modules/msgraphProxy/commands/Get-MsGraphProxyStatus
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
	$process = if ($state.Id) { Get-Process -Id $state.Id -ErrorAction SilentlyContinue }

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
