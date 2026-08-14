function Install-MsGraphProxyCertificate {
	<#
	.SYNOPSIS
		Trusts the running Dev Proxy instance's root CA certificate for the
		current user.

	.DESCRIPTION
		Fetches Dev Proxy's root CA certificate from its control API and trusts
		it for the current OS user, so HTTPS clients validate the certificates
		Dev Proxy generates for intercepted requests against a trusted CA,
		rather than an unknown one - confirmed end-to-end, including through
		the Microsoft Graph PowerShell SDK's own HTTP client, that this is
		sufficient on its own for a fully unmodified HTTPS call to validate
		cleanly, no client-side accommodation (skipping certificate validation
		or otherwise) needed.

		That wasn't always true: with certificate auto-install disabled -
		which Start-MsGraphProxy -CI does on Windows specifically, to avoid an
		interactive OS confirmation dialog blocking startup entirely - Dev
		Proxy's own unpatched behavior assigns its root CA itself as a single
		"generic" certificate served for every intercepted connection, instead
		of a proper per-domain leaf certificate, guaranteeing
		RemoteCertificateNameMismatch for any client that validates hostnames,
		regardless of whether the CA is trusted. Confirmed directly via a raw
		TLS handshake showing a served certificate of "CN=Dev Proxy CA" rather
		than a leaf cert for the requested host. This module's build pipeline
		(Build-DevProxyPackage.ps1) now patches that one line out of Dev
		Proxy's source so per-domain certificate generation always happens,
		decoupled from whether the (Windows-only) OS-trust attempt runs -
		trusting the CA via this function is genuinely sufficient now.

		On Windows this runs certutil, bounded by a timeout. Trusting a
		certificate into the Root store normally shows an interactive
		confirmation dialog - confirmed, across several different approaches
		(certutil, Import-Certificate, raw X509Store.Add(), a Scheduled Task
		registered with an explicit S4U logon), that none of them avoid it in a
		real, non-interactive GitHub Actions run; it consistently hangs rather
		than failing fast there. The timeout means this degrades to returning
		$false with a warning instead of hanging forever - which is the
		expected, normal outcome in CI specifically, not just a fallback for
		rare failures. It still works as intended on a genuine interactive
		desktop session, where the dialog can actually be answered.
		On Linux, the certificate is copied into the system trust store and
		update-ca-certificates is run (via sudo unless already running as
		root) - no interactive prompt is involved there.
		On macOS this runs `security add-trusted-cert` against the current
		user's login keychain (not the System keychain, so no sudo needed -
		the same target Dev Proxy's own built-in MacCertificateHelper uses),
		bounded by a timeout the same way as the Windows path: unlike Linux's
		update-ca-certificates, keychain trust changes are known on some
		macOS versions/headless setups to show a GUI authorization dialog,
		and that risk hasn't been confirmed one way or the other in a real,
		non-interactive CI run yet.

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
