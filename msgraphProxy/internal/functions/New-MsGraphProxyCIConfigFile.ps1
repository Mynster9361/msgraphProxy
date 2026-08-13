function New-MsGraphProxyCIConfigFile {
	<#
	.SYNOPSIS
		Derives a CI-friendly copy of a devproxyrc.json with certificate
		auto-install disabled.

	.DESCRIPTION
		With installCert true (Dev Proxy's default) on Windows, Dev Proxy awaits
		trusting its root CA into the OS certificate store before it starts
		listening on its proxy port at all - fine on an interactive desktop, but
		that trust step needs a confirmation dialog that never resolves in a
		non-interactive CI session, so the proxy port never binds. Setting
		installCert to false skips straight to loading the certificate without
		attempting OS trust, which is what actually unblocks the proxy from
		starting in CI - but it comes with a real cost, worth understanding: in
		dev-proxy's own source (ProxyEngine.cs), the installCert:false branch
		doesn't just skip OS trust, it also assigns the root CA itself as a
		single "generic" certificate served for every intercepted connection,
		bypassing normal per-domain certificate generation entirely - confirmed
		directly via a raw TLS handshake, which showed a presented certificate
		of "CN=Dev Proxy CA" instead of a leaf cert for the actual requested
		host. Any client that validates hostnames will always reject that,
		regardless of whether the CA itself is trusted.

		The blocking OS-trust attempt this whole thing exists to avoid is
		Windows-only (dev-proxy's ProxyServer is constructed with
		userTrustRootCertificate: RunTime.IsWindows), so installCert only needs
		to be forced false on Windows - Linux and macOS never had the deadlock
		risk in the first place, and forcing it there too was needlessly giving
		up correct per-domain certificate generation for no benefit. This
		function now only overrides installCert on Windows; Linux and macOS get
		the original config back unmodified (still copied alongside the
		original file, so callers always get a predictable path/port back).

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
	if ($IsWindows) {
		$config['installCert'] = $false
	}
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
