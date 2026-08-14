# msgraphProxy

A PowerShell wrapper around a self-contained [Dev Proxy](https://github.com/dotnet/dev-proxy) build, extended with
two custom plugins:

- **`GraphSchemaMockPlugin`** - mocks any Microsoft Graph v1.0 or beta endpoint straight from its real CSDL schema,
  no hand-written fixtures needed.
- **`EntraTokenMockPlugin`** - mocks the Entra ID token endpoint, so auth flows (including license-gated checks like
  Maester's) work end-to-end without a real app registration or tenant.

In short: point your Graph-calling PowerShell code at a fake tenant and get schema-accurate, fabricated responses
back - useful for testing, demos, and CI pipelines that shouldn't need a real Microsoft 365 tenant.

**Full documentation:** <https://mynster-it.dk/docs/modules/msgraphProxy>

> **AI disclaimer**
>
> The two mock plugins (`build/plugins-src/EntraTokenMockPlugin.cs`, `build/plugins-src/GraphSchemaMockPlugin.cs`)
> and the `.github/workflows/build-devproxy-binaries.yml` pipeline were heavily AI-assisted, guided and tested
> throughout by the module author. The rest of the PowerShell was written with a mix of both. All the research and
> domain knowledge needed to design the module in the first place is the author's own.

## Quick start

```powershell
Install-Module -Name msgraphProxy -Scope CurrentUser
Import-Module msgraphProxy

# One-time (or after a new Dev Proxy build is released): download the binaries for this OS
Install-MsGraphProxy

# Start Dev Proxy using the bundled configuration
Start-MsGraphProxy

# ...point your Graph calls at https://graph.microsoft.com as usual - Dev Proxy
# intercepts and mocks them while it's running...
# This also works for modules that end up pointing to msgraph so something like EntraAuth calls will also be mocked

Get-MsGraphProxyStatus
Stop-MsGraphProxy
```

That's it - no real tenant, no app registration, no `.NET` install required on the machine running it (Dev Proxy
ships as a self-contained, RID-specific build).

See [`sample/Show-Sample.ps1`](sample/Show-Sample.ps1) for a runnable end-to-end example, including calling Graph
with a real-looking token, calling it with no token at all, and reading back the minimal-permissions report.

## Commands

| Command | What it does |
| --- | --- |
| `Install-MsGraphProxy` | Downloads and caches the Dev Proxy build for your OS. |
| `Start-MsGraphProxy` | Starts Dev Proxy, installing it first if needed. |
| `Stop-MsGraphProxy` | Stops Dev Proxy and returns any recorded reports (e.g. minimal Graph permissions used). |
| `Get-MsGraphProxyStatus` | Reports whether Dev Proxy is currently running. |
| `Install-MsGraphProxyCertificate` | Trusts Dev Proxy's root certificate for the current user, best-effort. |

Every command has full comment-based help - run `Get-Help <command> -Full` for details, parameters and examples.

## Recording and minimal permissions

Recording starts automatically the moment Dev Proxy starts (pass `-NoRecord` to `Start-MsGraphProxy` to opt out).
Every Graph call made while it's running gets analyzed, and `Stop-MsGraphProxy` returns the results - including a
least-privilege permissions report, so you can find out exactly which Graph permissions a script or app actually
needs instead of guessing:

```powershell
Start-MsGraphProxy

# ...exercise the code you want a permissions report for...

$r = Stop-MsGraphProxy
$r.Recording.GraphMinimalPermissionsPlugin
```

```text
errors             : {}
minimalPermissions : {User.ReadBasic.All, Application.Read.All}
permissionsType    : Application
requests           : {@{method=GET; requestUrl=/users}, @{method=GET; requestUrl=/applications}}
```

`minimalPermissions` is the smallest set of Graph permissions that covers every request that was made - handy for
tightening an app registration's permissions down from "whatever seemed safe" to "exactly what's used."

> **Note:** not all endpoints have a least-privilege permission registered against them, so treat this as a strong
> starting point rather than a guaranteed-complete list.

## Running in CI

`Start-MsGraphProxy -CI` configures Dev Proxy for a non-interactive session: it routes Graph calls through the proxy
automatically and trusts the root certificate on a best-effort basis, so it also works unattended in a GitHub
Actions/Azure DevOps/etc. pipeline. `-EntraIDLicense` picks which Entra ID license tier the mocked tenant reports
(defaults to P2), for license-gated checks like Maester's `Get-MtLicenseInformation`.

See [`.github/workflows/maester-example.yml`](.github/workflows/maester-example.yml) for a full working example:
running [Maester](https://maester.dev) - a real, widely-used Entra/M365 security-testing framework - against this
mock instead of a real tenant.

## How it fits together

- `build/plugins-src/` - vendored copies of the two custom plugin `.cs` files. The build pipeline clones
  [dotnet/dev-proxy](https://github.com/dotnet/dev-proxy) fresh, drops these files into it, and publishes
  self-contained builds from that - the upstream repo itself is never modified.
- `build/Build-DevProxyPackage.ps1` - the script that does that cloning/building/packaging. Runs both locally and
  in CI.
- `msgraphProxy/config/` - the default `devproxyrc.json`, `mocks.json`, and the Graph v1.0/beta CSDL schemas gatheredd from [microsoftgraph/msgraph-metadata](https://github.com/microsoftgraph/msgraph-metadata/tree/master/schemas) repo and shipped
  with the module, so `Start-MsGraphProxy` works out of the box without depending on any file outside this repo.
- `msgraphProxy/functions/` - the public commands listed above.

Dev Proxy registers itself as the Windows system HTTP/HTTPS proxy while running, so every proxy-aware application on
the machine routes through it - it only decrypts and inspects hosts listed in `urlsToWatch` in the config (Microsoft
Graph and the Entra ID token endpoint, by default), tunnelling everything else through untouched.

`Stop-MsGraphProxy` always asks Dev Proxy to shut down gracefully through its control API first, so it can
unregister itself as the system proxy on its way out. It only force-kills as a last resort, and if it has to, it
also clears the Windows system-proxy registration itself - otherwise every proxy-aware app on the machine would be
left pointed at a dead port.
