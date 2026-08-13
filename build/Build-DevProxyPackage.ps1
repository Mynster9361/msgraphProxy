<#
.SYNOPSIS
    Builds self-contained Dev Proxy binaries from source and packages them
    as zip files, ready for a msgraphProxy GitHub release.

.DESCRIPTION
    Clones the upstream dotnet/dev-proxy repository into a temporary
    directory, adds this repository's vendored GraphSchemaMockPlugin and
    EntraTokenMockPlugin sources to it, publishes a self-contained build for
    each requested runtime identifier, and zips each one up.

    This always works against a fresh clone in the temp folder, so the
    original dev-proxy checkout on this machine, if any, is never touched.

.PARAMETER Rid
    One or more .NET runtime identifiers to build for.

.PARAMETER Ref
    Git branch of dotnet/dev-proxy to build from.

.PARAMETER OutputPath
    Directory to write the packaged zip files to.

.EXAMPLE
    PS C:\> .\build\Build-DevProxyPackage.ps1

    Builds win-x64, linux-x64 and osx-arm64 packages into .\package.
#>
[CmdletBinding()]
param (
    [string[]]
    # osx-x64 (Intel Mac) isn't included by default: GitHub Actions'
    # macos-latest runners are Apple Silicon, so there's no CI coverage to
    # verify an Intel build actually works - shipping it untested seemed
    # worse than not shipping it. Pass it explicitly if you need it anyway.
    $Rid = @('win-x64', 'linux-x64', 'osx-arm64'),

    [string]
    $Ref = 'main',

    [string]
    $OutputPath = (Join-Path -Path $PSScriptRoot -ChildPath '..\package')
)

$ErrorActionPreference = 'Stop'

$pluginsSourceRoot = Join-Path -Path $PSScriptRoot -ChildPath 'plugins-src'
$cloneRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "msgraphproxy-devproxy-$([guid]::NewGuid())"

if (-not (Test-Path -Path $OutputPath)) {
    New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
}

try {
    Write-Verbose "Cloning dotnet/dev-proxy@$Ref into $cloneRoot"
    git clone --branch $Ref --depth 1 https://github.com/dotnet/dev-proxy.git $cloneRoot

    Write-Verbose 'Adding msgraphProxy plugin sources'
    $pluginsMockingDir = Join-Path -Path $cloneRoot -ChildPath 'DevProxy.Plugins\Mocking'
    Copy-Item -Path (Join-Path -Path $pluginsSourceRoot -ChildPath '*.cs') -Destination $pluginsMockingDir -Force

    foreach ($currentRid in $Rid) {
        Write-Verbose "Publishing devproxy for $currentRid"
        $publishDir = Join-Path -Path $cloneRoot -ChildPath "dist\$currentRid"
        $devProxyProject = Join-Path -Path $cloneRoot -ChildPath 'DevProxy\DevProxy.csproj'

        dotnet publish $devProxyProject -c Release -r $currentRid --self-contained true -o $publishDir

        Write-Verbose "Building plugins for $currentRid"
        $pluginsProject = Join-Path -Path $cloneRoot -ChildPath 'DevProxy.Plugins\DevProxy.Plugins.csproj'
        dotnet build $pluginsProject -c Release -r $currentRid --no-self-contained

        $builtPluginsDir = Join-Path -Path $cloneRoot -ChildPath "DevProxy\bin\Release\net10.0\$currentRid\plugins"
        $publishedPluginsDir = Join-Path -Path $publishDir -ChildPath 'plugins'
        Copy-Item -Path $builtPluginsDir -Destination $publishedPluginsDir -Recurse -Force

        $zipPath = Join-Path -Path $OutputPath -ChildPath "msgraphproxy-devproxy-$currentRid.zip"
        if (Test-Path -Path $zipPath) {
            Remove-Item -Path $zipPath -Force
        }
        Compress-Archive -Path (Join-Path -Path $publishDir -ChildPath '*') -DestinationPath $zipPath

        Write-Verbose "Packaged $currentRid to $zipPath"
    }
}
finally {
    if (Test-Path -Path $cloneRoot) {
        Remove-Item -Path $cloneRoot -Recurse -Force
    }
}
