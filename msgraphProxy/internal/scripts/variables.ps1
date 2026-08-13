# Module-wide variables

$script:MsGraphProxyConfigRoot = Join-Path -Path $script:ModuleRoot -ChildPath 'config'
$script:MsGraphProxyDefaultConfigFile = Join-Path -Path $script:MsGraphProxyConfigRoot -ChildPath 'devproxyrc.json'
# $env:LOCALAPPDATA doesn't exist on Linux at all (confirmed: this failed
# Join-Path with a null Path argument the first time this module was ever
# imported there) - $XDG_DATA_HOME, falling back to ~/.local/share per the
# XDG Base Directory spec, is the Linux equivalent of "local app data".
# -AdditionalChildPath (not a literal 'msgraphProxy\bin' string) is what
# actually makes the result use the right separator per platform.
$dataRoot = if ($IsWindows) {
	$env:LOCALAPPDATA
} elseif ($env:XDG_DATA_HOME) {
	$env:XDG_DATA_HOME
} else {
	Join-Path -Path $HOME -ChildPath '.local/share'
}
$script:MsGraphProxyBinRoot = Join-Path -Path $dataRoot -ChildPath 'msgraphProxy' -AdditionalChildPath 'bin'
$script:MsGraphProxyStateFile = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'msgraphproxy-module-state.json'
$script:MsGraphProxyStdOutLog = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'msgraphproxy-devproxy-stdout.log'
$script:MsGraphProxyStdErrLog = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'msgraphproxy-devproxy-stderr.log'
$script:MsGraphProxyDefaultApiPort = 8897
$script:MsGraphProxyGitHubRepo = 'Mynster9361/msgraphProxy'
# $null on an unsupported OS rather than throwing here, so importing the module
# never fails outright - Install-MsGraphProxy/Get-MsGraphProxyExePath fall back
# to calling Get-MsGraphProxyRid directly, which throws its own clear error.
$script:MsGraphProxyRid = try { Get-MsGraphProxyRid } catch { $null }
