function Start-MsGraphProxy {
	<#
	.SYNOPSIS
		Starts the self-contained Dev Proxy build in its own process.
	
	.DESCRIPTION
		Launches the cached Dev Proxy executable against a devproxyrc.json
		configuration, and tracks the resulting process so Stop-MsGraphProxy and
		Get-MsGraphProxyStatus can find it again later, even from a different
		PowerShell session. If Dev Proxy hasn't been installed yet for this OS,
		it's installed automatically first (see Install-MsGraphProxy).
	
		Recording starts automatically with the proxy, so Stop-MsGraphProxy can
		stop it again and return the resulting reports (such as minimal Graph
		permissions) as an object. Pass -NoRecord to opt out.
	
		Dev Proxy registers itself as the Windows system HTTP/HTTPS proxy while
		running, so every proxy-aware application on the machine routes through
		it - it only decrypts and inspects hosts listed in its "urlsToWatch"
		configuration, tunnelling everything else through untouched.
	
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
		Configure Dev Proxy for a non-interactive session (CI pipelines, Pester
		runs, etc.) instead of normal interactive use. On Windows, certificate
		auto-install is disabled in the launched config - with it left on, Dev
		Proxy awaits an interactive OS confirmation dialog to trust its root CA
		before it even starts listening, which never resolves non-interactively
		and leaves the proxy port refusing every connection. That's a
		Windows-only risk (Dev Proxy only attempts OS trust automatically on
		Windows to begin with), so Linux and macOS keep certificate
		auto-install on - which also means they get Dev Proxy's normal,
		correct per-domain certificate generation, unlike Windows: disabling
		auto-install has a real side effect beyond skipping OS trust, covered
		in Install-MsGraphProxyCertificate's help, that this module's build
		pipeline patches around for Windows specifically.

		Instead of relying on Dev Proxy's system-wide proxy registration -
		Windows-only, and wasn't reliably picked up by other processes in
		testing anyway - this sets HTTP_PROXY/HTTPS_PROXY (and lowercase) for
		the current process, for non-.NET child processes started later in
		this session, and directly overrides
		[System.Net.Http.HttpClient]::DefaultProxy, since that's lazily
		evaluated from the environment once and then cached - setting the
		environment variables alone doesn't reliably reach PowerShell/.NET
		code running in *this* process. The root certificate is then trusted
		automatically via Install-MsGraphProxyCertificate on a best-effort
		basis - see its help for what "best-effort" means here, particularly
		on Windows. The returned object gains a CertificateTrusted property
		reflecting whether that succeeded; confirmed end-to-end (including
		through the Microsoft Graph PowerShell SDK's own HTTP client, not
		just Invoke-RestMethod) that once it's true, HTTPS calls validate
		cleanly with no client-side accommodation needed.

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
		$CI
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
	if ($CI) {
		$ciConfig = New-MsGraphProxyCIConfigFile -ConfigFile $resolvedConfigFile
		$resolvedConfigFile = $ciConfig.ConfigFile
		$proxyPort = $ciConfig.ProxyPort
	}

	# Start-Process doesn't quote array elements containing spaces itself, so
	# a config path with a space in it would otherwise be split into two
	# arguments on the receiving end.
	$processArgs = @('--config-file', "`"$resolvedConfigFile`"", '--api-port', $ApiPort)
	if (-not $NoRecord) {
		$processArgs += '--record'
	}

	$params = @{
		FilePath         = $exePath
		ArgumentList     = $processArgs
		WorkingDirectory = $(Split-Path -Path $exePath -Parent)
		PassThru         = $true
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
	if ($CI) {
		$proxyUri = "http://127.0.0.1:$proxyPort"
		foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'http_proxy', 'https_proxy') {
			[System.Environment]::SetEnvironmentVariable($name, $proxyUri, 'Process')
		}

		# The env vars above cover non-.NET child processes spawned later in
		# this session (curl, Node, Python, etc. - each reads them fresh at
		# its own startup). They're not enough on their own for .NET/PowerShell
		# code running in *this* process, though: HttpClient.DefaultProxy is
		# lazily evaluated from the environment once and then cached for the
		# rest of the process - confirmed directly, setting the env vars alone
		# still let a plain Invoke-RestMethod reach the real graph.microsoft.com
		# instead of this proxy. Overriding DefaultProxy directly takes effect
		# immediately regardless of that caching.
		[System.Net.Http.HttpClient]::DefaultProxy = [System.Net.WebProxy]::new($proxyUri)

		$certificateTrusted = $false
		if (Wait-MsGraphProxyControlApi -ApiPort $ApiPort) {
			$certificateTrusted = Install-MsGraphProxyCertificate -ApiPort $ApiPort
		} else {
			Write-Warning 'Dev Proxy did not become ready in time; skipping automatic certificate trust.'
		}

		$result = $result | Add-Member -NotePropertyName CertificateTrusted -NotePropertyValue $certificateTrusted -PassThru
	}

	$result
}
