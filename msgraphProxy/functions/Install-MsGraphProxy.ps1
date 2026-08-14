function Install-MsGraphProxy {
	<#
	.SYNOPSIS
		Downloads and installs the self-contained Dev Proxy build this module wraps.
	
	.DESCRIPTION
		Downloads the zipped, self-contained Dev Proxy build from the latest
		msgraphProxy GitHub release and extracts it into the module's local
		binary cache, so it can run without a .NET installation on this machine.

		Relying on GitHub's /releases/latest here only works because of how
		the two release pipelines in this repo divide that up: build.yml
		(the PowerShell-module release, on every push to main) is the one
		release meant to be "latest", and vsts-release.ps1 bundles that
		release with the most recent Dev Proxy binaries build alongside the
		module content - while build-devproxy-binaries.yml (which actually
		builds Dev Proxy from source together with the GraphSchemaMockPlugin
		and EntraTokenMockPlugin extensions this module depends on) publishes
		its own builds as prereleases specifically so they never compete for
		"latest" themselves. If that ever changes - either pipeline stops
		coordinating with the other - /releases/latest could return a release
		with no matching Rid asset, which is what the error below is for.
	
	.PARAMETER Rid
		The .NET runtime identifier to install a build for. Defaults to the RID
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
