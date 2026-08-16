function Wait-MsGraphProxyPort {
	<#
	.SYNOPSIS
		Waits for Dev Proxy's actual proxy port to start accepting connections.

	.DESCRIPTION
		Waits and checks that the proxy port has started listening and is ready for connections

	.PARAMETER ProxyPort
		The proxy's own listening port (not the control API port).

	.PARAMETER TimeoutSeconds
		How long to keep polling before giving up.

	.EXAMPLE
		PS C:\> Wait-MsGraphProxyPort -ProxyPort 8000

		Returns $true once the proxy port accepts a connection, or $false after 30 seconds.
	#>
	[CmdletBinding()]
	[OutputType([bool])]
	param (
		[Parameter(Mandatory)]
		[int]
		$ProxyPort,

		[int]
		$TimeoutSeconds = 30
	)

	$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
	while ((Get-Date) -lt $deadline) {
		try {
			$tcpClient = [System.Net.Sockets.TcpClient]::new()
			try {
				$tcpClient.Connect('127.0.0.1', $ProxyPort)
				return $true
			} finally {
				$tcpClient.Close()
			}
		} catch {
			Write-Verbose "Proxy port not ready yet: $_"
			Start-Sleep -Milliseconds 250
		}
	}

	return $false
}
