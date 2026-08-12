# Module-wide variables

$script:MsGraphProxyConfigRoot = Join-Path -Path $script:ModuleRoot -ChildPath 'config'
$script:MsGraphProxyDefaultConfigFile = Join-Path -Path $script:MsGraphProxyConfigRoot -ChildPath 'devproxyrc.json'
$script:MsGraphProxyBinRoot = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'msgraphProxy\bin'
$script:MsGraphProxyStateFile = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'msgraphproxy-module-state.json'
$script:MsGraphProxyDefaultApiPort = 8897
$script:MsGraphProxyGitHubRepo = 'Mynster9361/msgraphProxy'
