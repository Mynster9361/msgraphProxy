function Get-MsGraphProxyApiToken {
	<#
	.SYNOPSIS
		Reads the bearer token Dev Proxy requires for its control API.

	.DESCRIPTION
		Dev Proxy generates a random bearer token per instance and writes it
		to a file keyed by its own process ID - every control-API request
		(even a plain status GET) is rejected with 401 without it, matching
		its own 'devproxy status'/'devproxy logs' commands rediscover their
		target instance's token the same way. The file is written
		asynchronously during startup, so this polls for it rather than
		assuming it already exists the moment the process is spawned.

		Token file location matches Dev Proxy's own StateManager:
			Windows - %LocalAppData%\dev-proxy\credentials\api-<pid>.token
			macOS   - ~/Library/Application Support/dev-proxy/credentials/api-<pid>.token
			Linux   - $XDG_CONFIG_HOME/dev-proxy/credentials/api-<pid>.token (or ~/.config/dev-proxy/... )

	.PARAMETER ProcessId
		Process ID of the running Dev Proxy instance.

	.PARAMETER TimeoutSeconds
		How long to keep polling for the token file before giving up.

	.EXAMPLE
		PS C:\> Get-MsGraphProxyApiToken -ProcessId 12345

		Returns the bearer token for the Dev Proxy instance with that PID.
	#>
	[CmdletBinding()]
	[OutputType([string])]
	param (
		[Parameter(Mandatory)]
		[int]
		$ProcessId,

		[int]
		$TimeoutSeconds = 15
	)

	$configFolder = if ($IsWindows) {
		Join-Path -Path $env:LOCALAPPDATA -ChildPath 'dev-proxy'
	} elseif ($IsMacOS) {
		Join-Path -Path $HOME -ChildPath 'Library/Application Support/dev-proxy'
	} elseif ($env:XDG_CONFIG_HOME) {
		Join-Path -Path $env:XDG_CONFIG_HOME -ChildPath 'dev-proxy'
	} else {
		Join-Path -Path $HOME -ChildPath '.config/dev-proxy'
	}

	$tokenFile = Join-Path -Path $configFolder -ChildPath 'credentials' -AdditionalChildPath "api-$ProcessId.token"

	$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
	while ((Get-Date) -lt $deadline) {
		if (Test-Path -Path $tokenFile) {
			$token = (Get-Content -Path $tokenFile -Raw -ErrorAction SilentlyContinue)
			if ($token) {
				return $token.Trim()
			}
		}
		Start-Sleep -Milliseconds 250
	}

	throw "Dev Proxy's API token file never appeared at $tokenFile within $TimeoutSeconds seconds - the control API will reject every request without it."
}
