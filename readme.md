# msgraphProxy

>**AI DISCLAIMER**
>
>The root of this module "EntraTokenMockPlugin.cs", "GraphSchemaMockPlugin.cs" & "build-devproxy-binaries.yml" has been heavely inflused and build by AI but otherwise guided and tested by me
>
>The rest of the powershell code is dual operation so a bit of AI a bit of me and will be cleaned up before official launch of the module
>
>All required research and intel gathering for the core of the module in order to even build this module is done by my self

> This module is not yet released to PSGallery and is a work in progress
> The code that is currently in the repo might not work
> Code for Linux and Mac is only being tested in CICD so experience might vary

A PowerShell wrapper around a self-contained [Dev Proxy](https://github.com/dotnet/dev-proxy) build, extended with
two custom plugins - `GraphSchemaMockPlugin` (mocks any Microsoft Graph v1.0 endpoint from its CSDL schema, no
hand-written fixtures needed) and `EntraTokenMockPlugin` (mocks the Entra ID token endpoint so auth flows work
without a real app registration).

The Dev Proxy binaries this module runs require no .NET installation on the machine using it: they're built as
self-contained, RID-specific packages by this repository's own GitHub Actions pipeline (`.github/workflows/build-devproxy-binaries.yml`)
and downloaded on demand by `Install-MsGraphProxy`.

## How it fits together

- `build/plugins-src/` - vendored copies of the two custom plugin `.cs` files. The build pipeline clones
  [dotnet/dev-proxy](https://github.com/dotnet/dev-proxy) fresh, drops these files into it, and publishes
  self-contained builds from that - the upstream repo itself is never modified.
- `build/Build-DevProxyPackage.ps1` - the script that does that cloning/building/packaging. Runs both locally and
  in CI.
- `msgraphProxy/config/` - the default `devproxyrc.json`, `mocks.json` and Graph `v1.0.csdl` schema shipped with the
  module, so `Start-MsGraphProxy` works out of the box without depending on any file outside this repo.
- `msgraphProxy/functions/` - the public commands: `Install-MsGraphProxy`, `Start-MsGraphProxy`,
  `Stop-MsGraphProxy`, `Get-MsGraphProxyStatus`.

## Installation

```powershell
Install-Module -Name 'msgraphProxy' -Scope CurrentUser
```

## Usage

```powershell
Import-Module msgraphProxy

# One-time (or after a new Dev Proxy build is released): download the binaries for this OS
Install-MsGraphProxy

# Start Dev Proxy using the bundled configuration
Start-MsGraphProxy

Get-MsGraphProxyStatus

Stop-MsGraphProxy
```

Dev Proxy registers itself as the Windows system HTTP/HTTPS proxy while running, so every proxy-aware application on
the machine routes through it - it only decrypts and inspects hosts listed in `urlsToWatch` in the config (Microsoft
Graph and the Entra ID token endpoint, by default), tunnelling everything else through untouched.

`Stop-MsGraphProxy` always asks Dev Proxy to shut down gracefully through its control API first, so it can
unregister itself as the system proxy on its way out. It only force-kills as a last resort, and if it has to, it
also clears the Windows system-proxy registration itself - otherwise every proxy-aware app on the machine would be
left pointed at a dead port.
