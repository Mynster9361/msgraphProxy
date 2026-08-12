function Get-MsGraphProxyRid {
	<#
	.SYNOPSIS
		Resolves the .NET runtime identifier for the current operating system.
	
	.DESCRIPTION
		Maps the current operating system to the runtime identifier (RID) used to
		name the published, self-contained Dev Proxy binaries this module
		downloads and runs, for example "win-x64" or "linux-x64".
	
	.EXAMPLE
		PS C:\> Get-MsGraphProxyRid
	
		Returns the RID matching the current operating system, e.g. "win-x64".
	#>
	[CmdletBinding()]
	[OutputType([string])]
	param ()

	if ($IsWindows) {
		return 'win-x64'
	}
	if ($IsLinux) {
		return 'linux-x64'
	}
	throw 'msgraphProxy has no published Dev Proxy build for this operating system.'
}
