function Start-MsGraphProxy {
	<#
	.SYNOPSIS
		Starts the self-contained Dev Proxy build in its own process.
	
	.DESCRIPTION
		Launches the cached Dev Proxy executable, installed via Install-MsGraphProxy,
		against a devproxyrc.json configuration, and tracks the resulting process
		so Stop-MsGraphProxy and Get-MsGraphProxyStatus can find it again later,
		even from a different PowerShell session.
	
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
		$Force
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

	$exePath = Get-MsGraphProxyExePath
	if (-not $PSCmdlet.ShouldProcess('Dev Proxy', 'Start')) {
		return
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
	Get-MsGraphProxyStatus
}
