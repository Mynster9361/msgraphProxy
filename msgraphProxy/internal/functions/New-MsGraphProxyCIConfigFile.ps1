function New-MsGraphProxyCIConfigFile {
	<#
	.SYNOPSIS
		Derives a CI-friendly copy of a devproxyrc.json with certificate
		auto-install disabled.

	.DESCRIPTION
		With installCert true (Dev Proxy's default), Dev Proxy awaits trusting
		its root CA into the OS certificate store before it starts listening on
		its proxy port at all - fine on an interactive desktop, but that trust
		step needs a confirmation dialog that never resolves in a non-interactive
		CI session, so the proxy port never binds. Setting installCert to false
		skips straight to loading the certificate without attempting OS trust,
		which is what actually unblocks the proxy from starting in CI.

		The copy is written into the same directory as the original config file,
		not a temp directory - plugin settings like schemaFilePath/mocksFile are
		relative paths resolved against the config file's own directory, so
		keeping the copy alongside the original preserves those references
		without needing to know which keys hold them.

	.PARAMETER ConfigFile
		Path to the source devproxyrc.json to derive a CI copy from.

	.EXAMPLE
		PS C:\> New-MsGraphProxyCIConfigFile -ConfigFile 'C:\proxy\devproxyrc.json'

		Returns an object with the generated config's path and the proxy port
		it declares (or Dev Proxy's default of 8000 if unset).
	#>
	[CmdletBinding()]
	param (
		[Parameter(Mandatory)]
		[string]
		$ConfigFile
	)

	$config = Get-Content -Raw -Path $ConfigFile | ConvertFrom-Json -AsHashtable
	$config['installCert'] = $false
	$proxyPort = if ($config.ContainsKey('port')) { [int]$config['port'] } else { 8000 }

	# No leading dot: Dev Proxy's config-file resolution fails to find a
	# dot-prefixed filename (confirmed directly - it reports the file as not
	# found even though it exists at the exact path passed via --config-file).
	$ciConfigFile = Join-Path -Path (Split-Path -Path $ConfigFile -Parent) -ChildPath 'msgraphproxy-ci-devproxyrc.json'
	$config | ConvertTo-Json -Depth 10 | Set-Content -Path $ciConfigFile

	[pscustomobject]@{
		ConfigFile = $ciConfigFile
		ProxyPort  = $proxyPort
	}
}
