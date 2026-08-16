function Start-MsGraphProxy {
	<#
	.SYNOPSIS
		Starts the self-contained Dev Proxy build in its own process.
	
	.DESCRIPTION
		Launches the cached Dev Proxy executable against a devproxyrc.json
		configuration, and tracks the resulting process so Stop-MsGraphProxy
		and Get-MsGraphProxyStatus can find it again later, even from a
		different PowerShell session. If Dev Proxy hasn't been installed yet
		for this OS, it's installed automatically first (see
		Install-MsGraphProxy).

		Recording starts automatically with the proxy, so Stop-MsGraphProxy
		can stop it again and return the resulting reports (such as minimal
		Graph permissions) as an object. Pass -NoRecord to opt out.

		While running, Dev Proxy intercepts and mocks calls to the hosts
		listed in its "urlsToWatch" configuration (Microsoft Graph and the
		Entra ID token endpoint, by default), tunnelling everything else
		through untouched.

	.PARAMETER ConfigFile
		Path to a devproxyrc.json/.yaml configuration file. Defaults to the
		configuration bundled with this module.

	.PARAMETER ApiPort
		Port for Dev Proxy's control API, used by Stop-MsGraphProxy for a
		graceful shutdown. Defaults to Dev Proxy's own default port, 8897.

	.PARAMETER NoRecord
		Don't start recording automatically. Without this switch, Dev Proxy
		starts recording immediately so Stop-MsGraphProxy has something to stop
		and report on.

	.PARAMETER Force
		Start a new instance even if one is already tracked as running.

	.PARAMETER CI
		Configure Dev Proxy for a non-interactive session (CI pipelines,
		Pester runs, etc.) instead of normal interactive use: sets
		HTTP_PROXY/HTTPS_PROXY for the current session so Graph calls route
		through the proxy, and trusts Dev Proxy's root certificate
		automatically on a best-effort basis (see the returned object's
		CertificateTrusted property, and Install-MsGraphProxyCertificate's
		help for what "best-effort" means).

	.PARAMETER EntraIDLicense
		Which Entra ID license tier the mocked tenant's subscribedSkus should
		report - Free, P1, P2 or Governance. Defaults to P2, so license-gated
		checks (e.g. Maester's Get-MtLicenseInformation) see a licensed tenant
		out of the box. Pass -EntraIDLicense explicitly to pick a different
		tier.

	.PARAMETER WhatIf
		If this switch is enabled, no actions are performed but informational
		messages will be displayed that explain what would happen if the command
		were to run.
	
	.PARAMETER Confirm
		If this switch is enabled, you will be prompted for confirmation before
		executing any operations that change state.
	
	.EXAMPLE
		PS C:\> Start-MsGraphProxy
	
		Starts Dev Proxy using the configuration bundled with this module, recording from the start.
	
	.EXAMPLE
		PS C:\> Start-MsGraphProxy -ConfigFile 'C:\proxy\devproxyrc.json' -ApiPort 9000

		Starts Dev Proxy with a custom configuration and control-API port.

	.EXAMPLE
		PS C:\> Start-MsGraphProxy -CI

		Starts Dev Proxy configured for a CI pipeline: no certificate prompt to
		block startup, HTTP_PROXY/HTTPS_PROXY set for the current process, and
		its root certificate trusted automatically where possible.

	.LINK
		https://mynster-it.dk/docs/modules/msgraphProxy/commands/Start-MsGraphProxy
	#>
	[CmdletBinding(SupportsShouldProcess)]
	param (
		[string]
		$ConfigFile = $script:MsGraphProxyDefaultConfigFile,

		[int]
		$ApiPort = $script:MsGraphProxyDefaultApiPort,

		[switch]
		$NoRecord,

		[switch]
		$Force,

		[switch]
		$CI,

		[ValidateSet('Free', 'P1', 'P2', 'Governance')]
		[string]
		$EntraIDLicense = 'P2'
	)

	$existing = Get-MsGraphProxyStatus
	if ($existing.Running -and -not $Force) {
		Write-Warning "Dev Proxy is already running (PID $($existing.Id)). Use -Force to start another instance anyway."
		return $existing
	}

	if (-not (Test-Path -Path $ConfigFile)) {
		throw "Config file not found: $ConfigFile. Pass -ConfigFile explicitly, or run Install-MsGraphProxy if this is the bundled default."
	}
	$resolvedConfigFile = (Resolve-Path -Path $ConfigFile).Path

	if (-not $PSCmdlet.ShouldProcess('Dev Proxy', 'Start')) {
		return
	}

	try {
		$exePath = Get-MsGraphProxyExePath
	} catch {
		Write-Verbose 'Dev Proxy is not installed yet; installing it now.'
		$exePath = Install-MsGraphProxy
	}

	$proxyPort = 8000
	if ($CI -or $PSBoundParameters.ContainsKey('EntraIDLicense')) {
		$licensePreset = Get-MsGraphProxyEntraIDLicensePreset -License $EntraIDLicense
		$derivedConfig = New-MsGraphProxyCIConfigFile -ConfigFile $resolvedConfigFile -CI:$CI -SubscribedSkus $licensePreset
		$resolvedConfigFile = $derivedConfig.ConfigFile
		$proxyPort = $derivedConfig.ProxyPort
	} else {
		$rawConfig = Get-Content -Raw -Path $resolvedConfigFile | ConvertFrom-Json -AsHashtable
		if ($rawConfig.ContainsKey('port')) {
			$proxyPort = [int]$rawConfig['port']
		}
	}

	$processArgs = @('--config-file', "`"$resolvedConfigFile`"", '--api-port', $ApiPort)
	if (-not $NoRecord) {
		$processArgs += '--record'
	}

	if (Test-Path -Path $script:MsGraphProxyStdOutLog) { Remove-Item -Path $script:MsGraphProxyStdOutLog -Force }
	if (Test-Path -Path $script:MsGraphProxyStdErrLog) { Remove-Item -Path $script:MsGraphProxyStdErrLog -Force }

	$params = @{
		FilePath               = $exePath
		ArgumentList           = $processArgs
		WorkingDirectory       = $(Split-Path -Path $exePath -Parent)
		PassThru               = $true
		RedirectStandardOutput = $script:MsGraphProxyStdOutLog
		RedirectStandardError  = $script:MsGraphProxyStdErrLog
	}
	$process = Start-Process @params

	[pscustomobject]@{
		Id         = $process.Id
		ConfigFile = $resolvedConfigFile
		ExePath    = $exePath
		ApiPort    = $ApiPort
		Recording  = -not $NoRecord
		StartedAt  = (Get-Date).ToString('o')
	} | ConvertTo-Json | Set-Content -Path $script:MsGraphProxyStateFile

	Write-Verbose "Dev Proxy started (PID $($process.Id)) using $resolvedConfigFile"

	$result = Get-MsGraphProxyStatus
	$ready = Wait-MsGraphProxyControlApi -ApiPort $ApiPort
	if ($ready) {
		$ready = Wait-MsGraphProxyPort -ProxyPort $proxyPort
	}

	if (-not $ready) {
		Write-Warning 'Dev Proxy did not become ready to serve requests in time.'
	}

	if ($CI) {
		$proxyUri = "http://127.0.0.1:$proxyPort"
		foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'http_proxy', 'https_proxy') {
			[System.Environment]::SetEnvironmentVariable($name, $proxyUri, 'Process')
		}

		if ($env:GITHUB_ENV) {
			foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'http_proxy', 'https_proxy') {
				Add-Content -Path $env:GITHUB_ENV -Value "$name=$proxyUri"
			}
		}

		[System.Net.Http.HttpClient]::DefaultProxy = [System.Net.WebProxy]::new($proxyUri)

		$certificateTrusted = $false
		if ($ready) {
			$certificateTrusted = Install-MsGraphProxyCertificate -ApiPort $ApiPort
		} else {
			Write-Warning 'Skipping automatic certificate trust since Dev Proxy never became ready.'
		}

		$result = $result | Add-Member -NotePropertyName CertificateTrusted -NotePropertyValue $certificateTrusted -PassThru
	}

	$result
}
