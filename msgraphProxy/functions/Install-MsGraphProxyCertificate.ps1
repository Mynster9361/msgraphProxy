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
		interactive confirmation dialog - running certutil/Import-Certificate/
		X509Store.Add() directly all hit it, confirmed to hang rather than fail
		fast even in a genuinely non-interactive CI session. On an elevated
		session this first tries running certutil via a one-off Scheduled Task
		registered with an explicit S4U logon (confirmed to need elevation -
		registering one without it fails outright with Access Denied, which is
		exactly why this is only attempted when elevated): an S4U logon has no
		window station attached at all, so there's nothing for the dialog to
		attach to. GitHub Actions' Windows runners run elevated by default, so
		this is expected to help there specifically; on a normal (non-elevated)
		interactive desktop it's skipped and this falls back to running certutil
		directly, bounded by a timeout, in case that dialog is shown and nothing
		can answer it - this returns $false with a warning instead of hanging
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
			$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

			if ($isElevated) {
				$taskName = "MsGraphProxyCertTrust_$([guid]::NewGuid().ToString('N'))"
				try {
					$action = New-ScheduledTaskAction -Execute 'certutil.exe' -Argument "-addstore -f -user Root `"$certPath`""
					$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U
					Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -ErrorAction Stop | Out-Null
					Start-ScheduledTask -TaskName $taskName -ErrorAction Stop

					$deadline = (Get-Date).AddSeconds(15)
					do {
						Start-Sleep -Milliseconds 500
						$info = Get-ScheduledTaskInfo -TaskName $taskName
					} while ($info.LastTaskResult -eq 267009 -and (Get-Date) -lt $deadline)

					$trusted = $info.LastTaskResult -eq 0
					if (-not $trusted) {
						Write-Verbose "Certificate trust via Scheduled Task didn't succeed (LastTaskResult: $($info.LastTaskResult)); falling back to a direct attempt."
					}
				} catch {
					Write-Verbose "Certificate trust via Scheduled Task failed to even register/start ($_); falling back to a direct attempt."
				} finally {
					Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
				}
			}

			if (-not $trusted) {
				# Direct fallback: what's left once the Scheduled Task path is
				# unavailable (not elevated) or didn't pan out. Bounded by a
				# timeout since this is exactly the call expected to hit the
				# interactive confirmation dialog in that case.
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
