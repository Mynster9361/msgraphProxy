function Install-MsGraphProxyCertificate {
	<#
	.SYNOPSIS
		Trusts the running Dev Proxy instance's root CA certificate for the
		current user.

	.DESCRIPTION
		Fetches Dev Proxy's root CA certificate from its control API and
		trusts it for the current OS user, so HTTPS clients accept the
		certificates Dev Proxy generates for intercepted requests without any
		client-side accommodation (like skipping certificate validation).

		Supported on Windows (via certutil), Linux (via
		update-ca-certificates) and macOS (via the current user's login
		keychain). This is best-effort, not guaranteed: trusting a
		certificate can require an interactive confirmation dialog, which
		will never resolve in a non-interactive session (most commonly hit
		via Start-MsGraphProxy -CI). Rather than hang waiting for it, this
		function waits up to 15 seconds and then returns $false with a
		warning instead of throwing, so callers can decide for themselves
		whether to fall back to skipping certificate validation in their own
		requests. On a genuine interactive desktop session, trust normally
		succeeds and the confirmation dialog (Windows only) can just be
		answered.

	.PARAMETER ApiPort
		Port of Dev Proxy's control API.

	.EXAMPLE
		PS C:\> Install-MsGraphProxyCertificate

		Fetches and trusts the root certificate of the Dev Proxy instance
		using the default control-API port.

	.LINK
		https://mynster-it.dk/docs/modules/msgraphProxy/commands/Install-MsGraphProxyCertificate
	#>
	[CmdletBinding()]
	[OutputType([bool])]
	param (
		[int]
		$ApiPort = $script:MsGraphProxyDefaultApiPort
	)

	if (-not $IsWindows -and -not $IsLinux -and -not $IsMacOS) {
		Write-Warning 'Trusting the Dev Proxy root certificate automatically is only implemented for Windows, Linux and macOS.'
		return $false
	}

	$certPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'msgraphproxy-devproxy-ca.crt'
	try {
		Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/proxy/rootCertificate?format=crt" -OutFile $certPath -TimeoutSec 15
	} catch {
		Write-Warning "Couldn't fetch the Dev Proxy root certificate: $_"
		return $false
	}

	try {
		if ($IsWindows) {
			$psi = [System.Diagnostics.ProcessStartInfo]::new('certutil.exe')
			foreach ($arg in @('-addstore', '-f', '-user', 'Root', $certPath)) {
				$psi.ArgumentList.Add($arg)
			}
			$psi.RedirectStandardOutput = $true
			$psi.RedirectStandardError = $true
			$psi.UseShellExecute = $false

			$process = [System.Diagnostics.Process]::Start($psi)
			if (-not $process.WaitForExit(15000)) {
				$process.Kill()
				Write-Warning 'Trusting the Dev Proxy root certificate timed out, likely waiting on an interactive confirmation prompt that nothing could answer. HTTPS clients may need to skip certificate validation instead.'
				return $false
			}

			if ($process.ExitCode -ne 0) {
				$errorOutput = $process.StandardError.ReadToEnd()
				Write-Warning "certutil failed to trust the Dev Proxy root certificate (exit $($process.ExitCode)): $errorOutput"
				return $false
			}
		} elseif ($IsLinux) {
			$isRoot = (& id -u) -eq '0'
			$dest = '/usr/local/share/ca-certificates/msgraphproxy-devproxy.crt'
			if ($isRoot) {
				Copy-Item -Path $certPath -Destination $dest
				update-ca-certificates | Out-Null
			} else {
				sudo cp $certPath $dest
				sudo update-ca-certificates | Out-Null
			}

			if ($LASTEXITCODE -ne 0) {
				Write-Warning "update-ca-certificates failed (exit $LASTEXITCODE) to trust the Dev Proxy root certificate."
				return $false
			}
		} else {
			$keychain = Join-Path -Path $HOME -ChildPath 'Library/Keychains/login.keychain-db'
			$psi = [System.Diagnostics.ProcessStartInfo]::new('security')
			foreach ($arg in @('add-trusted-cert', '-r', 'trustRoot', '-k', $keychain, $certPath)) {
				$psi.ArgumentList.Add($arg)
			}
			$psi.RedirectStandardOutput = $true
			$psi.RedirectStandardError = $true
			$psi.UseShellExecute = $false

			$process = [System.Diagnostics.Process]::Start($psi)
			if (-not $process.WaitForExit(15000)) {
				$process.Kill()
				Write-Warning 'Trusting the Dev Proxy root certificate timed out, likely waiting on a keychain authorization prompt that nothing could answer. HTTPS clients may need to skip certificate validation instead.'
				return $false
			}

			if ($process.ExitCode -ne 0) {
				$errorOutput = $process.StandardError.ReadToEnd()
				Write-Warning "security failed to trust the Dev Proxy root certificate (exit $($process.ExitCode)): $errorOutput"
				return $false
			}
		}
	} finally {
		Remove-Item -Path $certPath -Force -ErrorAction SilentlyContinue
	}

	Write-Verbose 'Dev Proxy root certificate trusted.'
	$true
}
