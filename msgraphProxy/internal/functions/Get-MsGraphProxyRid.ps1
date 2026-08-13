function Get-MsGraphProxyRid {
	<#
	.SYNOPSIS
		Resolves the .NET runtime identifier for the current operating system.
	
	.DESCRIPTION
		Maps the current operating system to the runtime identifier (RID) used to
		name the published, self-contained Dev Proxy binaries this module
		downloads and runs, for example "win-x64" or "linux-x64".

		macOS resolves to "osx-arm64" or "osx-x64" depending on processor
		architecture - only osx-arm64 is actually built/published today (it's
		what GitHub Actions' macos-latest runners are), so Install-MsGraphProxy
		on an Intel Mac will fail clearly with "no release asset found" rather
		than this function silently mismapping it.

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
	if ($IsMacOS) {
		if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
			return 'osx-arm64'
		}
		return 'osx-x64'
	}
	throw 'msgraphProxy has no published Dev Proxy build for this operating system.'
}
