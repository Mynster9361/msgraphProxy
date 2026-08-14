function New-MsGraphProxyCIConfigFile {
	<#
	.SYNOPSIS
		Derives a modified copy of a devproxyrc.json - certificate auto-install
		disabled for CI, and/or config overrides like a specific Entra ID
		license.

	.DESCRIPTION
		Used by Start-MsGraphProxy to produce a working copy of devproxyrc.json
		whenever -CI or -EntraIDLicense is passed, rather than mutating the
		config file bundled with the module itself.

		-CI disables certificate auto-install on Windows, which is what lets
		Dev Proxy's proxy port actually bind in a non-interactive session -
		see the code comment where it's applied for why, and why it's only
		ever done under -CI specifically.

		-SubscribedSkus overwrites graphSchemaMockPlugin.subscribedSkus, which
		is how Start-MsGraphProxy's -EntraIDLicense picks which license tier
		the mocked tenant reports (see that plugin's own config shape).

		The copy is written next to the original config file, not to a temp
		directory: plugin settings like schemaFilePath/mocksFile are relative
		paths resolved against the config file's own directory, so keeping
		the copy alongside the original preserves those references.

	.PARAMETER ConfigFile
		Path to the source devproxyrc.json to derive a copy from.

	.PARAMETER CI
		Also disable certificate auto-install on Windows - see DESCRIPTION.

	.PARAMETER SubscribedSkus
		Replaces graphSchemaMockPlugin.subscribedSkus from the source config.
		Only applied if -SubscribedSkus is passed.

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

	$ciConfigFile = Join-Path -Path (Split-Path -Path $ConfigFile -Parent) -ChildPath 'msgraphproxy-ci-devproxyrc.json'
	if ($PSCmdlet.ShouldProcess($ciConfigFile, 'Write derived Dev Proxy config')) {
		$config | ConvertTo-Json -Depth 10 | Set-Content -Path $ciConfigFile
	}

	[pscustomobject]@{
		ConfigFile = $ciConfigFile
		ProxyPort  = $proxyPort
	}
}
