function Clear-MsGraphProxySystemProxy {
	<#
	.SYNOPSIS
		Clears a Windows system-proxy registration left behind by Dev Proxy.
	
	.DESCRIPTION
		Dev Proxy registers itself as the Windows system HTTP/HTTPS proxy while
		running, and only unregisters that as part of its own graceful shutdown.
		If Dev Proxy has to be force-killed instead, that unregistration never
		runs, and Windows is left pointed at a dead proxy port, breaking every
		proxy-aware application on the machine. This is a last-resort safety net
		that mirrors what Dev Proxy's own graceful shutdown does, called by
		Stop-MsGraphProxy only when the graceful stop failed.
	
	.EXAMPLE
		PS C:\> Clear-MsGraphProxySystemProxy
	
		Disables the Windows system proxy if Dev Proxy left it enabled.
	#>
	[CmdletBinding()]
	param ()

	if (-not $IsWindows) {
		return
	}

	$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
	$current = Get-ItemProperty -Path $key -Name ProxyEnable -ErrorAction SilentlyContinue
	if (-not $current -or $current.ProxyEnable -eq 0) {
		return
	}

	Set-ItemProperty -Path $key -Name ProxyEnable -Value 0

	if (-not ('MsGraphProxyModule.WinInet' -as [type])) {
		Add-Type -Namespace MsGraphProxyModule -Name WinInet -MemberDefinition @'
[DllImport("wininet.dll", SetLastError = true)]
public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
'@
	}

	[MsGraphProxyModule.WinInet]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0) | Out-Null
	[MsGraphProxyModule.WinInet]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0) | Out-Null

	Write-Warning 'Cleared a stale Windows system-proxy registration left behind by Dev Proxy.'
}
