function New-MsGraphProxyCIConfigFile {
	<#
	.SYNOPSIS
		Derives a modified copy of a devproxyrc.json - certificate auto-install
		disabled for CI, and/or config overrides like a specific Entra ID
		license.

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
		regardless of whether the CA itself is trusted. So this is only ever
		applied when -CI is passed - never unconditionally - or a normal
		interactive Windows caller asking only for e.g. a different license
		would silently lose correct per-domain certificate generation too.

		The blocking OS-trust attempt this whole thing exists to avoid is
		Windows-only (dev-proxy's ProxyServer is constructed with
		userTrustRootCertificate: RunTime.IsWindows), so installCert only needs
		to be forced false on Windows under -CI - Linux and macOS never had the
		deadlock risk in the first place, and forcing it there too was
		needlessly giving up correct per-domain certificate generation for no
		benefit.

		The copy is written into the same directory as the original config file,
		not a temp directory - plugin settings like schemaFilePath/mocksFile are
		relative paths resolved against the config file's own directory, so
		keeping the copy alongside the original preserves those references
		without needing to know which keys hold them.

		While rewriting the config anyway, this is also where -SubscribedSkus
		from Start-MsGraphProxy gets applied: it overwrites
		graphSchemaMockPlugin.subscribedSkus so callers can pick which
		Entra ID/other license the mocked tenant has (e.g. what
		Get-MtLicenseInformation would report) without hand-editing
		devproxyrc.json.

	.PARAMETER ConfigFile
		Path to the source devproxyrc.json to derive a copy from.

	.PARAMETER CI
		Also disable certificate auto-install on Windows - see DESCRIPTION.

	.PARAMETER SubscribedSkus
		Replaces graphSchemaMockPlugin.subscribedSkus from the source config -
		see that plugin's own config shape. Only applied if -SubscribedSkus is
		passed.

	.PARAMETER WhatIf
		If this switch is enabled, no actions are performed but informational
		messages will be displayed that explain what would happen if the command
		were to run.

	.PARAMETER Confirm
		If this switch is enabled, you will be prompted for confirmation before
		executing any operations that change state.

	.EXAMPLE
		PS C:\> New-MsGraphProxyCIConfigFile -ConfigFile 'C:\proxy\devproxyrc.json' -CI

		Returns an object with the generated config's path and the proxy port
		it declares (or Dev Proxy's default of 8000 if unset).
	#>
	[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
	param (
		[Parameter(Mandatory)]
		[string]
		$ConfigFile,

		[switch]
		$CI,

		[object[]]
		$SubscribedSkus
	)

	$config = Get-Content -Raw -Path $ConfigFile | ConvertFrom-Json -AsHashtable
	if ($CI -and $IsWindows) {
		$config['installCert'] = $false
	}

	if ($PSBoundParameters.ContainsKey('SubscribedSkus')) {
		if (-not $config.ContainsKey('graphSchemaMockPlugin')) {
			$config['graphSchemaMockPlugin'] = @{}
		}
		$config['graphSchemaMockPlugin']['subscribedSkus'] = @($SubscribedSkus)
	}

	$proxyPort = if ($config.ContainsKey('port')) { [int]$config['port'] } else { 8000 }

	# No leading dot: Dev Proxy's config-file resolution fails to find a
	# dot-prefixed filename (confirmed directly - it reports the file as not
	# found even though it exists at the exact path passed via --config-file).
	$ciConfigFile = Join-Path -Path (Split-Path -Path $ConfigFile -Parent) -ChildPath 'msgraphproxy-ci-devproxyrc.json'
	if ($PSCmdlet.ShouldProcess($ciConfigFile, 'Write derived Dev Proxy config')) {
		$config | ConvertTo-Json -Depth 10 | Set-Content -Path $ciConfigFile
	}

	[pscustomobject]@{
		ConfigFile = $ciConfigFile
		ProxyPort  = $proxyPort
	}
}
