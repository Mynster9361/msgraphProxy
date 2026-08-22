# Changelog

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
