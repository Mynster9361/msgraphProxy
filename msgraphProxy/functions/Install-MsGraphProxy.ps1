function Install-MsGraphProxy {
	<#
	.SYNOPSIS
		Downloads and installs the self-contained Dev Proxy build this module wraps.
	
	.DESCRIPTION
		Downloads the zipped, self-contained Dev Proxy build (bundled with this
		module's GraphSchemaMockPlugin and EntraTokenMockPlugin extensions)
		from this repository's latest GitHub release, and extracts it into the
		module's local binary cache - no separate DOTNET installation needed on
		this machine.

		If a build for the target RID is already cached, this does nothing
		unless -Force is passed. Start-MsGraphProxy calls this automatically
		the first time it needs to, so you normally don't need to call it
		yourself.

	.PARAMETER Rid
		The DOTNET runtime identifier to install a build for. Defaults to the RID
		matching the current operating system.

	.PARAMETER Force
		Reinstall even if a build for this RID is already cached.

	.EXAMPLE
		PS C:\> Install-MsGraphProxy

		Downloads and installs the Dev Proxy build matching the current OS.

	.EXAMPLE
		PS C:\> Install-MsGraphProxy -Force

		Re-downloads and reinstalls the Dev Proxy build, replacing whatever is
		already cached.

	.LINK
		https://mynster-it.dk/docs/modules/msgraphProxy/commands/Install-MsGraphProxy
	#>
	[CmdletBinding()]
	param (
		[string]
		$Rid = ($script:MsGraphProxyRid ?? (Get-MsGraphProxyRid)),

		[switch]
		$Force
	)

	$ridRoot = Join-Path -Path $script:MsGraphProxyBinRoot -ChildPath $Rid
	$exeName = if ($IsWindows) { 'devproxy.exe' } else { 'devproxy' }
	$exePath = Join-Path -Path $ridRoot -ChildPath $exeName

	if ((Test-Path -Path $exePath) -and -not $Force) {
		Write-Verbose "Dev Proxy for $Rid is already installed at $ridRoot."
		return $exePath
	}

	$releaseUri = "https://api.github.com/repos/$($script:MsGraphProxyGitHubRepo)/releases/latest"
	$release = Invoke-RestMethod -Uri $releaseUri -Headers @{ 'User-Agent' = 'msgraphProxy' }

	$asset = $release.assets | Where-Object Name -Like "*$Rid*.zip" | Select-Object -First 1
	if (-not $asset) {
		throw "No release asset found for $Rid in release $($release.tag_name). Available assets: $($release.assets.name -join ', ')"
	}

	$zipPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath $asset.name
	Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing

	if (Test-Path -Path $ridRoot) {
		Remove-Item -Path $ridRoot -Recurse -Force
	}
	New-Item -Path $ridRoot -ItemType Directory -Force | Out-Null
	Expand-Archive -Path $zipPath -DestinationPath $ridRoot -Force
	Remove-Item -Path $zipPath -Force

	if ($IsLinux -or $IsMacOS) {
		& chmod +x $exePath
	}

	Write-Verbose "Installed Dev Proxy $($release.tag_name) for $Rid to $ridRoot."
	$exePath
}
