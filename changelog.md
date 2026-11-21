# Changelog

## 1.2.0 (2026-11-21)

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
+ Change: `$select` with an unknown property now gets Graph's 400 (`Could not find a property named 'x' on type 'T'.`). Properties of the type, its base types and its derived types are accepted, so polymorphic collections like `members` still work. Matching is case-insensitive, and `*` and `extension_*` names are always allowed. Set `graphSchemaMockPlugin.validateSelect` to `false` for clients that select properties this CSDL doesn't declare.
+ Change: contained navigations hold their own data per parent. `/users/A/messages` and `/users/B/messages` are different records, and a record from one parent is a 404 under another. Root collections keep their ids.
+ Add: `graphSchemaMockPlugin.validatePathKeys` makes an unknown id anywhere in the path a 404, like Graph. Off by default.
+ Change: non-contained navigations (`members`, `memberOf`, `owners`, `manager`, `directReports`, ...) are a seeded relationship graph. They reference real records of the right type, so members are users and memberOf returns groups, and the records resolve at `/users/{id}` or `/groups/{id}`. `$ref` reads and writes change it: GET lists references, POST adds one (adding the same member twice now gets Graph's 400 instead of a second 204), DELETE `.../{id}/$ref` removes one, and PUT sets a to-one reference. `$expand` uses the same graph.
+ Add: `/directoryObjects/{id}` (and any root set of a base type) resolves a user, group or other derived record, with `@odata.type`.
+ Add: cast segments (`/members/microsoft.graph.user`, `/members/graph.user`) filter to a derived type, also with `$count`. Items reached through a relationship carry `@odata.type`. `/directory/deletedItems/{type}` is empty in a fresh mock tenant.
+ Change: with a seed, generated values depend only on each pool's own key, so they no longer depend on which records were requested first.
+ Add: a control endpoint at `https://graph.microsoft.com/_msgraphproxy/`, watched by the shipped `devproxyrc.json`. Control calls are never counted or throttled.
  + `GET stats`: requests, `$batch` sub-requests, non-GET requests, 429s served, faults per rule, `Retry-After` violations.
  + `POST reset` (optionally `{"seed": n}`): fresh pools, snapshots, fault state, counters and clock. With the same seed, it matches a newly started mock byte for byte.
  + `GET|POST clock` (`{"now": "..."}` or `{"now": null}`) and `POST clock/advance` (`{"seconds": n}` or `?seconds=n`).
  + `GET|PUT scenario`: replaces the fault rules, buckets and mailbox limit at runtime. `{}` turns faults off.
+ Add: fault injection and throttling through `graphSchemaMockPlugin.faults`. Everything is off until configured.
  + `rules`: match by path glob (`*` is one segment, `**` is any), method, query text, header, and `DirectOnly`/`BatchOnly`. Each rule returns a status with an optional `Retry-After`, `probability` (drawn from the seed when set), `everyNth`, `maxHits`, or pure `latencyMs`.
  + `mailboxConcurrencyLimit` (Graph's is 4): more concurrent requests to one mailbox get 429 `ApplicationThrottled`. Parallel `$batch` sub-requests to the same mailbox count together.
  + `buckets`: token buckets per workload, `Global`, `App` or `Mailbox`. Responses carry `x-ms-throttle-limit-percentage` once a bucket is at least 80% used.
  + `Retry-After` is enforced: retrying early gets another 429 and counts as a violation. All timing follows the engine clock, so with `fixedNow` the clock has to be advanced.
  + The engine exposes stats (requests, `$batch` sub-requests, non-GET requests, 429s served, faults per rule, `Retry-After` violations) for the upcoming control endpoint.
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
