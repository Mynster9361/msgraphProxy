function Install-MsGraphProxyCertificate {
	<#
	.SYNOPSIS
		Trusts the running Dev Proxy instance's root CA certificate for the
		current user.

	.DESCRIPTION
		Fetches Dev Proxy's root CA certificate from its control API and trusts
		it for the current OS user, so HTTPS clients validate the certificates
		Dev Proxy generates for intercepted requests without needing to skip
		certificate validation themselves - useful for code that doesn't offer
		an easy way to do that (most Graph SDKs and HTTP clients don't).

		On Windows, trusting a certificate into the Root store normally shows an
		interactive confirmation dialog. certutil and raw X509Store.Add() both
		show a real, hangable dialog for it on an interactive session, and a
		Scheduled Task registered with an explicit S4U logon (which has no
		window station attached at all) didn't avoid that in real CI testing
		either. Import-Certificate behaves differently, though: on an
		interactive session it fails immediately ("UI is not allowed in this
		operation") rather than showing anything, which is worth trying first -
		a clean fast failure is cheap, and it may behave differently again (or
		even succeed) in a genuinely non-interactive session, which is worth
		confirming for real rather than assumed. It still runs in a background
		job bounded by a timeout regardless, in case that assumption doesn't
		hold either. Falls back to certutil directly (also bounded) if that
		doesn't pan out - this returns $false with a warning instead of hanging
		forever, either way.
		On Linux, the certificate is copied into the system trust store and
		update-ca-certificates is run (via sudo unless already running as
		root) - no interactive prompt is involved there.

		Returns $false rather than throwing when trust couldn't be established,
		since this is inherently best-effort and callers - most commonly
		Start-MsGraphProxy -CI - should be able to decide for themselves
		whether to fall back to skipping certificate validation in their own
		requests instead.

	.PARAMETER ApiPort
		Port of Dev Proxy's control API.

	.EXAMPLE
		PS C:\> Install-MsGraphProxyCertificate

		Fetches and trusts the root certificate of the Dev Proxy instance
		using the default control-API port.
	#>
	[CmdletBinding()]
	[OutputType([bool])]
	param (
		[int]
		$ApiPort = $script:MsGraphProxyDefaultApiPort
	)

	if (-not $IsWindows -and -not $IsLinux) {
		Write-Warning 'Trusting the Dev Proxy root certificate automatically is only implemented for Windows and Linux.'
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
			$trusted = $false

			# Run via a background job so it's killable, not just fast in
			# practice - this is exactly the kind of assumption ("it fails
			# fast, so it can't hang") this module has been wrong about before.
			$importJob = Start-Job -ScriptBlock {
				param($Path)
				Import-Certificate -FilePath $Path -CertStoreLocation 'Cert:\CurrentUser\Root' -ErrorAction Stop | Out-Null
			} -ArgumentList $certPath

			if ((Wait-Job -Job $importJob -Timeout 15) -and $importJob.State -eq 'Completed') {
				$trusted = $true
			} else {
				$importError = Receive-Job -Job $importJob -ErrorAction SilentlyContinue 2>&1
				Write-Verbose "Import-Certificate didn't trust the certificate (state: $($importJob.State), error: $importError); falling back to certutil."
			}
			Remove-Job -Job $importJob -Force -ErrorAction SilentlyContinue

			if (-not $trusted) {
				# Fallback: certutil directly, bounded by a timeout since this
				# is exactly the call expected to hit the interactive
				# confirmation dialog if Import-Certificate's fast-fail
				# behavior doesn't translate into an actual successful trust.
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
			}
		} else {
			$isRoot = (& id -u) -eq '0'
			$dest = '/usr/local/share/ca-certificates/msgraphproxy-devproxy.crt'
			if ($isRoot) {
				cp $certPath $dest
				update-ca-certificates | Out-Null
			} else {
				sudo cp $certPath $dest
				sudo update-ca-certificates | Out-Null
			}

			if ($LASTEXITCODE -ne 0) {
				Write-Warning "update-ca-certificates failed (exit $LASTEXITCODE) to trust the Dev Proxy root certificate."
				return $false
			}
		}
	} finally {
		Remove-Item -Path $certPath -Force -ErrorAction SilentlyContinue
	}

	Write-Verbose 'Dev Proxy root certificate trusted.'
	$true
}
