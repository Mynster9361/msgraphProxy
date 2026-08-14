# Module-wide variables

$script:MsGraphProxyConfigRoot = Join-Path -Path $script:ModuleRoot -ChildPath 'config'
$script:MsGraphProxyDefaultConfigFile = Join-Path -Path $script:MsGraphProxyConfigRoot -ChildPath 'devproxyrc.json'
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
$script:MsGraphProxyRid = try { Get-MsGraphProxyRid } catch { $null }
