# Changelog

## Unreleased

+ Change: GraphSchemaMockPlugin's mock logic now lives in an engine-agnostic `MockEngine` (build/plugins-src/MockEngine/) with no Dev Proxy dependencies. The plugin is a thin Dev Proxy v3 adapter. Behavior is unchanged; all smoke checks pass.
+ Add: `GraphMockHttpMessageHandler` runs the same engine in-process behind an `HttpClient`, with no proxy, certificates or network.
+ Change: Build-DevProxyPackage.ps1 copies plugin sources recursively so subfolders are compiled.
+ Fix: requests to `dod-graph.microsoft.us` were watched but never mocked, so they went to the real DoD Graph endpoint. Dev Proxy's `IsGraphUrl` doesn't match that host; the new `GraphHosts` registry does.
+ Fix: the mock engine is now thread-safe. Dev Proxy runs plugins concurrently, and parallel writes could be lost. Lookups by id now use an index instead of a linear scan.
+ Add: `graphSchemaMockPlugin.strict` returns a 404/405 Graph error for anything the mock can't answer instead of forwarding it to the real service. Off by default.
+ Add: paging for collection GETs. Supports `$top`, `Prefer: odata.maxpagesize` (acknowledged with `Preference-Applied`), an opaque `$skiptoken` and `@odata.nextLink`, which keeps the other query options. The defaults match Graph's directory APIs: 100 per page and at most 999; `$top` outside that range gets Graph's 400. `@odata.count` is now the total, not the page size. Configure it with `graphSchemaMockPlugin.paging`: `defaultPageSize`, `maxPageSize`, per-type `pageSizes`, and `consistency` (`Snapshot`, the default, freezes the result set on page 1; `Offset` re-evaluates every page).
+ Add: reproducible mocks through `graphSchemaMockPlugin.determinism`. With a `seed`, the same requests give byte-identical responses: ids are name-based (record *i* of a type is always the same id), and generated GUIDs, request-ids and `$skiptoken`s follow the seed. `fixedNow` pins the clock for generated dates and the `Date` header. `defaultPoolSize` and per-type `poolSizes` (for example 500 users) set how many records are seeded.
+ Change: entity results of functions (`delta()`, `getAllSites()`, ...) and `$expand`ed navigation properties now come from the type's pool, so their ids resolve on a follow-up GET. Before, they were fresh, unresolvable fabrications.
+ Fix: the `Date` response header was local time labeled as GMT. It is now UTC.
+ Add: each `$batch` sub-response has its own `headers` object: `Content-Type` (JSON or text/plain), plus headers the same request would get at the top level, such as `Preference-Applied`.
+ Fix: `$batch` sub-requests used a hard-coded `graph.microsoft.com`, so nextLinks and `@odata.context` inside a US Gov, DoD or China batch pointed at the wrong cloud. They now use the host the batch was sent to.
+ Change: an absolute URL in a `$batch` sub-request now gets its own 400 instead of being quietly re-rooted.
+ Change: collections with more than 100 items now return 100 per page with a nextLink, like Graph. Before, they returned everything at once.
+ Add: `graphSchemaMockPlugin.additionalGraphHosts` for extra Graph hosts, such as sovereign clouds. They must also be listed in `urlsToWatch`.

## 1.1.1 (2026-10-09)

+ Fix: New devproxy release added security arround the proxy api meaning we needed a token in order to see queries processed.
+ Fix: Updated devproxy instance to fix a issue with the graph permission analysis as the endpoint was updated on Microsoft side from: "https://devxapi-func-prod-eastus.azurewebsites.net/permissions" to: "https://graph-devx-api.microsoft.com/permissions"
+ Add: New command `Get-MsGraphProxyApiToken` which gets the token for the currently running proxy

## 1.1.0 (2026-10-09)

+ Add: New CSDL files for v1.0 and beta adding new endpoints to the proxy

## 1.0.4 (2026-08-22)

+ Add: Start-MsGraphProxy 2 new parameters `-WatchPid` and `-WatchProcessName` allows you to only capture network requests from either a specific service or process id

## 1.0.3 (2026-08-17)

+ Fix: order of operation on release caused the latest prerelease of the binaries to not pick up the actual latest but the one before so release always took 1 version before release.

## 1.0.2 (2026-08-17)

+ Fix: order of operation on release caused the latest prerelease of the binaries to not pick up the actual latest but the one before so release always took 1 version before release.

## 1.0.1 (2026-08-17)

+ Fix: Minor resolution to response data from mocking
+ Fix: Default (no `$select`) mock responses were silently dropping complex- and enum-typed properties (e.g. a conditional access policy's `conditions`/`grantControls`/`sessionControls`/`state`), keeping only scalars - callers reading those properties without an explicit `$select` got `null` instead of fabricated data
+ Fix: GET requests for PIM role management alerts (`identityGovernance/roleManagementAlerts/alerts/{id}`) always 404'd, since their well-known composite ids never matched the generic seeded-pool model - they now resolve to an inactive alert like a real tenant with no triggered alerts would

## 1.0.0 (2026-08-16)

+ New: Initial Release
