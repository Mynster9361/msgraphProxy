function Get-MsGraphProxyExePath {
	<#
	.SYNOPSIS
		Resolves the local path to the cached Dev Proxy executable.
	
	.DESCRIPTION
		Looks up the self-contained Dev Proxy build for the current operating
		system inside the module's local binary cache, installed there by
		Install-MsGraphProxy, and returns the full path to its executable.
	
	.EXAMPLE
		PS C:\> Get-MsGraphProxyExePath
	
		Returns the full path to the cached devproxy executable, throwing if it
		hasn't been installed yet.
	#>
	[CmdletBinding()]
	param ()

	$rid = Get-MsGraphProxyRid
	$exeName = if ($IsWindows) { 'devproxy.exe' } else { 'devproxy' }
	$ridRoot = Join-Path -Path $script:MsGraphProxyBinRoot -ChildPath $rid
	$exePath = Join-Path -Path $ridRoot -ChildPath $exeName

	if (-not (Test-Path -Path $exePath)) {
		throw "Dev Proxy isn't installed for $rid. Run Install-MsGraphProxy first."
	}

	$exePath
}
