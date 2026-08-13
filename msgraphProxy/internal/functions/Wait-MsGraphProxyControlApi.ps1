function Wait-MsGraphProxyControlApi {
	<#
	.SYNOPSIS
		Waits for Dev Proxy's control API to start responding.

	.DESCRIPTION
		Polls the control API (plain HTTP, no TLS involved) until it responds
		or the timeout elapses. Dev Proxy needs a moment after being started to
		bind this endpoint, so callers that need to know it's actually up -
		before fetching its root certificate, for example - poll rather than
		assume a fixed delay is enough.

	.PARAMETER ApiPort
		Port of Dev Proxy's control API.

	.PARAMETER TimeoutSeconds
		How long to keep polling before giving up.

	.EXAMPLE
		PS C:\> Wait-MsGraphProxyControlApi -ApiPort 8897

		Returns $true once the control API responds, or $false after 30 seconds.
	#>
	[CmdletBinding()]
	[OutputType([bool])]
	param (
		[Parameter(Mandatory)]
		[int]
		$ApiPort,

		[int]
		$TimeoutSeconds = 30
	)

	$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
	while ((Get-Date) -lt $deadline) {
		try {
			Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/proxy" -TimeoutSec 5 | Out-Null
			return $true
		} catch {
			Write-Verbose "Control API not ready yet: $_"
			Start-Sleep -Seconds 1
		}
	}

	return $false
}
