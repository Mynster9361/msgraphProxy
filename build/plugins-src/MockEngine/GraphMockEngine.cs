// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace MsGraphProxy.MockEngine;

public sealed class GraphSchemaMockServicePlanConfiguration
{
    public string ServicePlanId { get; set; } = "";
    public string? ServicePlanName { get; set; }
    public string? ProvisioningStatus { get; set; } = "Success";
}

// Callers rarely care about a subscribedSku's full real shape (accountId,
// subscriptionIds, etc. - all still generically fabricated) - only that
// capabilityStatus and servicePlans carry values license-detection logic
// (e.g. Maester's Get-MtLicenseInformation) actually keys off of, which the
// generic fabricator can't produce since it doesn't know what a real
// "Enabled" status or a real Entra ID P2 service plan GUID look like.
public sealed class GraphSchemaMockSubscribedSkuConfiguration
{
    public string SkuPartNumber { get; set; } = "";
    public string CapabilityStatus { get; set; } = "Enabled";
    public int PrepaidUnitsEnabled { get; set; } = 10;
    public int ConsumedUnits { get; set; } = 1;
    public IEnumerable<GraphSchemaMockServicePlanConfiguration> ServicePlans { get; set; } = [];
}

/// <summary>
/// Generates mock responses for any Microsoft Graph v1.0 or beta endpoint by
/// resolving the request path against the real CSDL schema for that version
/// (EntitySets, Singletons and NavigationProperties) and fabricating a
/// schema-accurate object on the fly, instead of requiring a pre-authored
/// fixture per endpoint.
/// </summary>
/// <remarks>
/// Engine-agnostic: no Dev Proxy, Titanium or logging dependencies, so the same engine runs behind the
/// Dev Proxy v3 adapter (GraphSchemaMockPlugin), an in-process HttpMessageHandler, or a later v4 adapter.
/// </remarks>
public sealed partial class GraphMockEngine
{
    private const int MaxDepth = 3;
    private const string V1VersionSegment = "/v1.0/";
    private const string BetaVersionSegment = "/beta/";

    private sealed class TypeDef
    {
        public required string BaseType { get; init; }
        public List<(string Name, string Type)> Properties { get; } = [];
        public List<(string Name, string Type)> NavigationProperties { get; } = [];
    }

    private sealed class FunctionActionDef
    {
        public required string Name { get; init; }
        public required bool IsAction { get; init; }
        public required bool IsCollectionBound { get; init; }
        public string? ReturnType { get; init; }
    }

    // Mirrors the subset of OData's Org.OData.Capabilities.V1.* vocabulary that
    // actually changes response shape/validity for this plugin's purposes -
    // whether $filter/$orderby/$expand are allowed at all for a resource, and
    // which individual properties are excluded. These annotations are real,
    // already present in Microsoft's own published CSDL (confirmed directly:
    // e.g. the "users" entity set's ExpandRestrictions lists "chats",
    // "joinedTeams", "onPremisesSyncBehavior", "permissionGrants" and "teamwork"
    // as non-expandable) - without reading them, this plugin was strictly more
    // permissive than the real API, letting $filter/$orderby/$expand usage pass
    // against the mock that the real service would reject with a 400.
    private sealed class QueryRestrictions
    {
        public bool Filterable { get; set; } = true;
        public HashSet<string> NonFilterableProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Sortable { get; set; } = true;
        public HashSet<string> NonSortableProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Expandable { get; set; } = true;
        public HashSet<string> NonExpandableProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // Everything parsed out of one CSDL file. v1.0 and beta each get their own
    // instance - the two schemas can (and do) disagree about a type's shape, so
    // resolving a request always has to go through exactly one of these, never a
    // shared/mutable "current schema" field (this plugin's requests can run
    // concurrently, so that would be a race condition).
    private sealed class SchemaRegistry
    {
        public Dictionary<string, string> AliasMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TypeDef> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> EnumTypes { get; } = new(StringComparer.Ordinal);
        // Keyed by the resolved type each Function/Action's bindingParameter targets.
        public Dictionary<string, List<FunctionActionDef>> FunctionsByBindingType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<FunctionActionDef>> ActionsByBindingType { get; } = new(StringComparer.Ordinal);
        // Keyed case-insensitively: Graph's URL routing (and thus segments typed by
        // callers) is case-insensitive for entity set / singleton / navigation
        // property names, e.g. "approleassignments" resolves the same as
        // "appRoleAssignments" against the real service.
        public Dictionary<string, string> EntitySets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Singletons { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Restrictions declared directly against an entity type (e.g. Target=
        // "microsoft.graph.bitlockerRecoveryKey") - the overwhelmingly dominant
        // pattern in Graph's real CSDL (confirmed: 73/73 FilterRestrictions,
        // 96/101 ExpandRestrictions occurrences target a type this way).
        public Dictionary<string, QueryRestrictions> TypeRestrictions { get; } = new(StringComparer.Ordinal);
        // Restrictions declared against a specific entity set or singleton (e.g.
        // Target="microsoft.graph.GraphService/users") - rarer, but takes
        // precedence over the type-level default when present, same as real
        // OData annotation resolution. This is where "users" own chats/
        // joinedTeams/etc. exclusions actually live.
        public Dictionary<string, QueryRestrictions> EntitySetRestrictions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="ItemId"/> is set only when the *last* path segment consumed was
    /// an id selecting one item out of a collection - i.e. exactly the cases where
    /// existence should be checked against the in-memory store. It is null for bare
    /// collections, singletons, and to-one navigation results, since under the
    /// one-pool-per-type model those aren't identified by a specific id.
    ///
    /// <paramref name="IsRef"/>, <paramref name="IsValue"/>, <paramref name="IsCount"/> and
    /// <paramref name="Operation"/> are mutually exclusive terminal markers - ResolvePath only
    /// ever sets one of them, immediately before returning. When <paramref name="IsValue"/> is
    /// set, <paramref name="TypeFullName"/> is repurposed to hold the resolved property's Edm
    /// type (not an entity type) and <paramref name="PropName"/> holds its real name.
    ///
    /// <paramref name="SetOrSingletonName"/> is set only immediately after resolving
    /// the top-level entity set / singleton segment (segments[0]), and carried
    /// through a plain collection-to-item transition (the same entity set, just one
    /// item of it) - but reset to null the moment the path navigates through a
    /// navigation property, since a restriction declared against, say, "users" as an
    /// entity set has no bearing on some other type reached from it.
    /// </summary>
    private readonly record struct ResolvedNode(
        string TypeFullName,
        bool IsCollection,
        string? ItemId,
        bool IsRef = false,
        bool IsValue = false,
        bool IsCount = false,
        FunctionActionDef? Operation = null,
        string? PropName = null,
        string? SetOrSingletonName = null);

    private static readonly string[] UserDefaultProperties =
    [
        "id", "businessPhones", "displayName", "givenName", "jobTitle", "mail",
        "mobilePhone", "officeLocation", "preferredLanguage", "surname", "userPrincipalName",
    ];

    private static readonly string[] SubscribedSkuDefaultProperties =
    [
        "id", "accountId", "accountName", "appliesTo", "capabilityStatus",
        "consumedUnits", "prepaidUnits", "servicePlans", "skuId", "skuPartNumber", "subscriptionIds",
    ];

    // The specific set of Microsoft Entra ID (directory) object types real
    // Graph gates several $filter/$count behaviors behind "advanced query"
    // mode for - ne/not/endsWith in $filter, $count (as a URL segment, query
    // parameter, or combined with $orderby) - all require the caller to set
    // the ConsistencyLevel: eventual header (and, except for $search, pass
    // $count=true too). Confirmed directly against
    // https://learn.microsoft.com/graph/aad-advanced-queries: this is an
    // explicit, narrow allow-list in the real API, not a blanket rule -
    // every other resource in this mock stays exactly as permissive as
    // before. $search itself isn't implemented by this plugin at all yet
    // (a separate, pre-existing gap), so it isn't gated here either.
    private static readonly HashSet<string> AdvancedQueryDirectoryObjectTypes = new(StringComparer.Ordinal)
    {
        "microsoft.graph.administrativeUnit",
        "microsoft.graph.application",
        "microsoft.graph.appRoleAssignment",
        "microsoft.graph.device",
        "microsoft.graph.group",
        "microsoft.graph.oAuth2PermissionGrant",
        "microsoft.graph.orgContact",
        "microsoft.graph.servicePrincipal",
        "microsoft.graph.user",
    };

    private static bool HasConsistencyLevelEventual(MockRequest request) =>
        string.Equals(request.GetHeader("ConsistencyLevel"), "eventual", StringComparison.OrdinalIgnoreCase);

    private static bool HasCountTrue(MockRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        return string.Equals(query["$count"], "true", StringComparison.OrdinalIgnoreCase);
    }

    // Mirrors the real error shape for this class of rejection - confirmed
    // against the doc's own example: same code, same message template
    // (including the aka.ms link), just with the operator name substituted.
    private static JsonObject AdvancedQueryRequiredError(string operatorName) =>
        ODataError(
            "Request_UnsupportedQuery",
            $"Operator '{operatorName}' is not supported because the required parameters might be missing. Try adding $count=true query parameter and ConsistencyLevel:eventual header. Refer to https://aka.ms/graph-docs/advanced-queries for more information");

    private static readonly Dictionary<string, Func<string>> NameFakes = new(StringComparer.Ordinal)
    {
        ["id"] = () => MockAmbient.NewGuid("prop:id").ToString(),
        ["givenName"] = () => "Test",
        ["surname"] = () => "User",
        ["userPrincipalName"] = () => "testuser@contoso.com",
        ["mail"] = () => "testuser@contoso.com",
        ["mailNickname"] = () => "testuser",
        ["jobTitle"] = () => "Developer",
        ["officeLocation"] = () => "Building 1",
        ["preferredLanguage"] = () => "en-US",
        ["mobilePhone"] = () => "+1 4255550100",
        ["city"] = () => "Seattle",
        ["country"] = () => "United States",
        ["usageLocation"] = () => "US",
        ["companyName"] = () => "Contoso",
        ["department"] = () => "Engineering",
    };

    private readonly SchemaRegistry _v1Registry;
    private readonly SchemaRegistry? _betaRegistry;
    private readonly List<GraphSchemaMockSubscribedSkuConfiguration> _subscribedSkus;

    // One shared pool of generated records per resolved type, seeded lazily on
    // first access and mutated by POST/PATCH/PUT/DELETE, so repeated requests
    // within a session see consistent, workable data instead of fresh random
    // values every call. Shared across v1.0 and beta: they model the same
    // underlying tenant data, just through differently-shaped API surfaces, so a
    // record seeded via one version's schema is reused (and simply missing
    // whichever properties are beta-only/v1.0-only) rather than kept separate.
    private readonly Dictionary<string, EntityPool> _store = new(StringComparer.Ordinal);

    // One lock for the whole engine: pools are shared across types, $batch sub-requests
    // and $expand, and responses are serialized inside Handle, so request-level
    // serialization is the simplest correct model. Dev Proxy invokes plugins concurrently.
    private readonly Lock _gate = new();

    private readonly GraphMockPagingOptions _paging;
    private readonly GraphMockDeterminismOptions _determinism;
    private readonly MockEntropy _entropy;

    // Snapshot-mode result sets, keyed by the id inside the $skiptoken. Bounded: the oldest are evicted,
    // and a token pointing at an evicted snapshot gets the same 400 an expired real token does.
    private const int MaxSnapshots = 256;
    private readonly Dictionary<string, List<JsonObject>> _snapshots = new(StringComparer.Ordinal);
    private readonly Queue<string> _snapshotOrder = new();

    // Extra headers for the response being built by the current ResolveAndProcess call (e.g.
    // Preference-Applied). Safe as a field: everything runs under _gate, and ResolveAndProcess
    // clears it first, so $batch sub-requests never see each other's headers.
    private List<KeyValuePair<string, string>>? _pendingHeaders;

    /// <param name="v1Csdl">Contents of the v1.0 CSDL.</param>
    /// <param name="betaCsdl">Contents of the beta CSDL, or null to leave /beta/* unmocked.</param>
    /// <param name="subscribedSkus">Seeds the subscribedSkus pool instead of generic fabrication, when non-empty.</param>
    /// <param name="paging">Page sizes and $skiptoken consistency; defaults match Graph's directory APIs.</param>
    /// <param name="determinism">Seed, fixed clock and pool sizes; defaults keep the historical random behavior.</param>
    public GraphMockEngine(
        string v1Csdl,
        string? betaCsdl = null,
        IEnumerable<GraphSchemaMockSubscribedSkuConfiguration>? subscribedSkus = null,
        GraphMockPagingOptions? paging = null,
        GraphMockDeterminismOptions? determinism = null)
    {
        ArgumentNullException.ThrowIfNull(v1Csdl);
        _paging = paging ?? new GraphMockPagingOptions();
        _determinism = determinism ?? new GraphMockDeterminismOptions();
        _entropy = new MockEntropy(_determinism.Seed);
        Clock = new MockClock(_determinism.FixedNow);
        _v1Registry = ParseSchema(v1Csdl);
        _betaRegistry = betaCsdl is null ? null : ParseSchema(betaCsdl);
        _subscribedSkus = subscribedSkus?.ToList() ?? [];
    }

    /// <summary>The engine's clock: real time, or a fixed "now" that only moves when advanced.</summary>
    public MockClock Clock { get; }

    /// <summary>Entity types, enums, entity sets and singletons parsed for a version, for host logging.</summary>
    public SchemaStats GetSchemaStats(string version)
    {
        var registry = version == "beta" ? _betaRegistry : _v1Registry;
        return registry is null
            ? new SchemaStats(false, 0, 0, 0, 0)
            : new SchemaStats(true, registry.Types.Count, registry.EnumTypes.Count, registry.EntitySets.Count, registry.Singletons.Count);
    }

    /// <summary>
    /// Resolves one request against the schema. The caller decides which hosts are Graph;
    /// the engine only looks at the method, path, query, headers and body.
    /// </summary>
    public MockOutcome Handle(string method, MockRequest request)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            using var ambient = MockAmbient.Enter(_entropy, Clock);
            return HandleCore(method, request);
        }
    }

    /// <summary>
    /// The response strict mode sends instead of forwarding a request the engine didn't handle,
    /// so nothing ever reaches the real service. Shapes match what $batch sub-requests already get.
    /// </summary>
    public MockResponse StrictResponse(MockSkipReason reason, MockRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            using var ambient = MockAmbient.Enter(_entropy, Clock);
            return BuildStrictResponse(reason, request);
        }
    }

    private static MockResponse BuildStrictResponse(MockSkipReason reason, MockRequest request)
    {
        var result = reason is MockSkipReason.UnsupportedMethod or MockSkipReason.MethodNotSupportedForResource
            ? new MockResult(ODataError("Request_BadRequest", "Specified HTTP method is not allowed for the request target."), HttpStatusCode.MethodNotAllowed, LogLabel: $"strict: {reason}")
            : new MockResult(ODataError("Request_ResourceNotFound", $"Resource not found for the segment '{request.RequestUri.AbsolutePath}'."), HttpStatusCode.NotFound, LogLabel: $"strict: {reason}");
        return ToResponse(result, request);
    }

    private MockOutcome HandleCore(string method, MockRequest request)
    {
        method = method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE"))
        {
            return MockOutcome.NotHandled(MockSkipReason.UnsupportedMethod);
        }

        var path = request.RequestUri.AbsolutePath;
        var (registry, versionSegment) = ResolveRegistry(path);
        if (registry is null)
        {
            return MockOutcome.NotHandled(MockSkipReason.UnknownVersion);
        }

        // POST {version}/$batch is its own top-level shape - see
        // https://learn.microsoft.com/graph/json-batching - never routed
        // through the normal EntitySet/Singleton resolution below.
        var remainder = path[(path.IndexOf(versionSegment, StringComparison.OrdinalIgnoreCase) + versionSegment.Length)..].Trim('/');
        if (method == "POST" && string.Equals(remainder, "$batch", StringComparison.OrdinalIgnoreCase))
        {
            return MockOutcome.Respond(ToResponse(HandleBatch(registry, versionSegment, request), request));
        }

        var result = ResolveAndProcess(registry, versionSegment, method, path, request);
        if (result is null)
        {
            return MockOutcome.NotHandled(MockSkipReason.NoSchemaMatch);
        }

        if (result.Value.StatusCode is null)
        {
            // method not supported for this resource shape - let it fall through
            return MockOutcome.NotHandled(MockSkipReason.MethodNotSupportedForResource);
        }

        return MockOutcome.Respond(ToResponse(result.Value, request));
    }

    // v1.0 is checked first so a path containing both segments (never happens in
    // practice, but not worth relying on) resolves deterministically.
    private (SchemaRegistry? Registry, string VersionSegment) ResolveRegistry(string path)
    {
        if (path.Contains(V1VersionSegment, StringComparison.OrdinalIgnoreCase))
        {
            return (_v1Registry, V1VersionSegment);
        }

        if (_betaRegistry is not null && path.Contains(BetaVersionSegment, StringComparison.OrdinalIgnoreCase))
        {
            return (_betaRegistry, BetaVersionSegment);
        }

        return (null, "");
    }

    // --- $batch (https://learn.microsoft.com/graph/json-batching) ---

    // The doc states batching supports "up to 20" individual requests but
    // doesn't give a worked example of what happens past that limit or for
    // a duplicate id - those two rejection messages below are a reasonable
    // approximation, not a confirmed-verbatim match, unlike the $count/
    // $search error text elsewhere in this plugin which was checked against
    // real documented examples.
    private const int MaxBatchRequests = 20;

    private MockResult HandleBatch(SchemaRegistry registry, string versionSegment, MockRequest request)
    {
        var outerBody = ParseBody(request);
        if (outerBody is null || outerBody["requests"] is not JsonArray requestsArray)
        {
            return new MockResult(ODataError("BadRequest", "The batch request body must be a JSON object with a 'requests' array."), HttpStatusCode.BadRequest, LogLabel: "$batch: malformed body");
        }

        if (requestsArray.Count > MaxBatchRequests)
        {
            return new MockResult(ODataError("BadRequest", $"The batch request can contain at most {MaxBatchRequests} individual requests."), HttpStatusCode.BadRequest, LogLabel: "$batch: too many requests");
        }

        var subRequests = new List<(string Id, string Method, string Url, JsonObject? Headers, string? Body, List<string> DependsOn)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in requestsArray)
        {
            if (item is not JsonObject sub ||
                sub["id"] is not JsonValue idNode || !idNode.TryGetValue(out string? id) || string.IsNullOrWhiteSpace(id) ||
                sub["method"] is not JsonValue methodNode || !methodNode.TryGetValue(out string? subMethod) || string.IsNullOrWhiteSpace(subMethod) ||
                sub["url"] is not JsonValue urlNode || !urlNode.TryGetValue(out string? url) || string.IsNullOrWhiteSpace(url))
            {
                return new MockResult(ODataError("BadRequest", "Each entry in 'requests' must have a non-empty 'id', 'method' and 'url'."), HttpStatusCode.BadRequest, LogLabel: "$batch: malformed sub-request");
            }

            // Not case-sensitive per the doc, and must be unique in the
            // batch or the whole batch request fails with a 400 - both
            // confirmed directly in the doc's own property table, just
            // without a verbatim example error message.
            if (!seenIds.Add(id))
            {
                return new MockResult(ODataError("BadRequest", $"Duplicate request id '{id}' in batch."), HttpStatusCode.BadRequest, LogLabel: "$batch: duplicate id");
            }

            var dependsOn = sub["dependsOn"] is JsonArray dependsOnArray
                ? dependsOnArray.Select(d => d is JsonValue dv && dv.TryGetValue(out string? dep) ? dep : null).Where(d => d is not null).Select(d => d!).ToList()
                : [];

            subRequests.Add((id, subMethod.ToUpperInvariant(), url, sub["headers"] as JsonObject, sub["body"] is { } bodyNode ? bodyNode.ToJsonString() : null, dependsOn));
        }

        foreach (var sub in subRequests)
        {
            var unknownDependency = sub.DependsOn.FirstOrDefault(d => !seenIds.Contains(d));
            if (unknownDependency is not null)
            {
                return new MockResult(ODataError("BadRequest", $"Request '{sub.Id}' depends on unknown request id '{unknownDependency}'."), HttpStatusCode.BadRequest, LogLabel: "$batch: unknown dependsOn id");
            }
        }

        var results = new Dictionary<string, SubResponse>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Id, string Method, string Url, JsonObject? Headers, string? Body, List<string> DependsOn)>(subRequests);

        // "Batch should be either fully sequential or fully parallel" per
        // the doc - this mock only ever executes synchronously regardless,
        // so what actually matters observably is honoring dependsOn
        // ordering and propagating 424 (Failed Dependency) when a
        // dependency didn't succeed, both of which this loop does.
        while (pending.Count > 0)
        {
            var progressed = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var sub = pending[i];
                if (!sub.DependsOn.TrueForAll(results.ContainsKey))
                {
                    continue;
                }

                var failedDependency = sub.DependsOn.FirstOrDefault(d => results[d].Status >= 400);
                results[sub.Id] = failedDependency is not null
                    ? SubResponse.Json(424, ODataError("Request_Dependency", $"Request depends on '{failedDependency}', which failed."))
                    : ProcessBatchSubRequest(registry, versionSegment, request.RequestUri, sub.Method, sub.Url, sub.Headers, sub.Body);

                pending.RemoveAt(i);
                progressed = true;
            }

            if (!progressed)
            {
                // A circular dependsOn chain - not something a well-formed
                // client would send, but fail loud rather than loop forever.
                foreach (var sub in pending)
                {
                    results[sub.Id] = SubResponse.Json(400, ODataError("BadRequest", $"Request '{sub.Id}' is part of a circular dependsOn chain."));
                }

                break;
            }
        }

        // Real Graph doesn't guarantee response order matches request order
        // (confirmed in the doc - callers correlate via id), so emitting in
        // dependency-resolved order rather than reconstructing the original
        // array order is a faithful, not just convenient, choice.
        var responses = new JsonArray();
        foreach (var (id, sub) in results)
        {
            var entry = new JsonObject
            {
                ["id"] = id,
                ["status"] = sub.Status,
            };

            // Each sub-response carries its own headers, like Graph's (Content-Type, plus anything the
            // same request would get at the top level, e.g. Preference-Applied; Retry-After once #7 lands).
            if (sub.Headers.Count > 0)
            {
                var headerObject = new JsonObject();
                foreach (var (name, value) in sub.Headers)
                {
                    headerObject[name] = value;
                }

                entry["headers"] = headerObject;
            }

            entry["body"] = sub.Body?.DeepClone();
            responses.Add(entry);
        }

        return new MockResult(new JsonObject { ["responses"] = responses }, HttpStatusCode.OK, LogLabel: $"$batch: {subRequests.Count} sub-request(s)");
    }

    private readonly record struct SubResponse(int Status, JsonNode? Body, IReadOnlyList<KeyValuePair<string, string>> Headers)
    {
        public static SubResponse Json(int status, JsonNode? body, IReadOnlyList<KeyValuePair<string, string>>? extra = null)
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (body is not null)
            {
                headers.Add(new("Content-Type", "application/json"));
            }

            if (extra is not null)
            {
                headers.AddRange(extra);
            }

            return new SubResponse(status, body, headers);
        }
    }

    private SubResponse ProcessBatchSubRequest(SchemaRegistry registry, string versionSegment, Uri batchUri, string method, string url, JsonObject? headers, string? body)
    {
        // Sub-request urls must be relative to the $batch endpoint (e.g. "/me/memberOf" or
        // "users?$select=..."). An absolute url is rejected for that sub-request rather than silently
        // re-rooted, which would hide a client bug.
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return SubResponse.Json((int)HttpStatusCode.BadRequest, ODataError("BadRequest", $"Invalid request URL '{url}': batch request URLs must be relative to the $batch endpoint."));
        }

        // Prefixing with versionSegment and re-running the exact same resolve/dispatch path used
        // for a top-level request makes every $filter/$expand/$search/paging behavior work
        // identically inside a batch. The scheme and host are the outer request's, so nextLinks
        // and @odata.context point at the cloud the client actually called.
        var queryIdx = url.IndexOf('?', StringComparison.Ordinal);
        var query = queryIdx >= 0 ? url[queryIdx..] : "";
        var path = (queryIdx >= 0 ? url[..queryIdx] : url).TrimStart('/');
        var absoluteUri = new Uri($"{batchUri.Scheme}://{batchUri.Authority}{versionSegment}{path}{query}");

        var subRequest = new MockRequest(absoluteUri, name => GetJsonHeader(headers, name), body);

        var result = ResolveAndProcess(registry, versionSegment, method, absoluteUri.AbsolutePath, subRequest);
        if (result is null)
        {
            return SubResponse.Json((int)HttpStatusCode.NotFound, ODataError("Request_ResourceNotFound", $"Resource not found for the segment '{url}'."));
        }

        if (result.Value.StatusCode is null)
        {
            // Method genuinely unsupported for this resource shape (e.g. DELETE on a singleton):
            // the shape of the doc's own example ("Specified HTTP method is not allowed ...").
            return SubResponse.Json((int)HttpStatusCode.MethodNotAllowed, ODataError("Request_BadRequest", "Specified HTTP method is not allowed for the request target."));
        }

        var status = (int)result.Value.StatusCode.Value;
        if (result.Value.RawText is not null)
        {
            var textHeaders = new List<KeyValuePair<string, string>> { new("Content-Type", "text/plain") };
            return new SubResponse(status, JsonValue.Create(result.Value.RawText), textHeaders);
        }

        return SubResponse.Json(status, result.Value.Body, result.Value.ExtraHeaders);
    }

    private static string? GetJsonHeader(JsonObject? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var kvp in headers)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase) && kvp.Value is JsonValue v && v.TryGetValue(out string? s))
            {
                return s;
            }
        }

        return null;
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleCollection(SchemaRegistry registry, string versionSegment, string typeFullName, string method, MockRequest request, QueryRestrictions? restrictions)
    {
        var pool = GetOrSeedPool(registry, typeFullName);

        if (method == "GET")
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var filterParam = query["$filter"];
            var orderByParam = query["$orderby"];

            FilterNode? filterNode = null;
            if (!string.IsNullOrWhiteSpace(filterParam))
            {
                filterNode = TryParseFilter(filterParam);
                if (filterNode is null)
                {
                    return (ODataError("BadRequest", $"Unable to parse $filter '{filterParam}'."), HttpStatusCode.BadRequest);
                }

                if (restrictions is not null)
                {
                    if (!restrictions.Filterable)
                    {
                        return (ODataError("BadRequest", "$filter is not supported for this resource."), HttpStatusCode.BadRequest);
                    }

                    var filterProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    filterNode.CollectPropertyNames(filterProps);
                    var nonFilterable = filterProps.FirstOrDefault(restrictions.NonFilterableProperties.Contains);
                    if (nonFilterable is not null)
                    {
                        return (ODataError("BadRequest", $"Property '{nonFilterable}' is not filterable."), HttpStatusCode.BadRequest);
                    }
                }
            }

            List<OrderByTerm>? orderByTerms = null;
            if (!string.IsNullOrWhiteSpace(orderByParam))
            {
                orderByTerms = TryParseOrderBy(orderByParam);
                if (orderByTerms is null)
                {
                    return (ODataError("BadRequest", $"Unable to parse $orderby '{orderByParam}'."), HttpStatusCode.BadRequest);
                }

                if (restrictions is not null)
                {
                    if (!restrictions.Sortable)
                    {
                        return (ODataError("BadRequest", "$orderby is not supported for this resource."), HttpStatusCode.BadRequest);
                    }

                    var nonSortable = orderByTerms.Select(t => t.PropName).FirstOrDefault(restrictions.NonSortableProperties.Contains);
                    if (nonSortable is not null)
                    {
                        return (ODataError("BadRequest", $"Property '{nonSortable}' is not sortable."), HttpStatusCode.BadRequest);
                    }
                }
            }

            var expandError = ValidateExpand(registry, typeFullName, request, restrictions);
            if (expandError is not null)
            {
                return (expandError, HttpStatusCode.BadRequest);
            }

            // Advanced query gate (directory objects only) - see
            // https://learn.microsoft.com/graph/aad-advanced-queries.
            // ConsistencyLevel: eventual + $count=true unlock ne/not/endsWith
            // in $filter and combining $filter with $orderby; without them,
            // real Graph rejects the request outright rather than silently
            // degrading, so this mock does the same.
            var isDirectoryObjectType = AdvancedQueryDirectoryObjectTypes.Contains(typeFullName);
            var countTrue = HasCountTrue(request);
            var advancedQueryModeActive = countTrue && HasConsistencyLevelEventual(request);

            if (isDirectoryObjectType && filterNode is not null)
            {
                var advancedOperators = new List<string>();
                filterNode.CollectAdvancedOperators(advancedOperators);
                if (advancedOperators.Count > 0 && !advancedQueryModeActive)
                {
                    return (AdvancedQueryRequiredError(advancedOperators[0]), HttpStatusCode.BadRequest);
                }

                if (orderByTerms is not null && !advancedQueryModeActive)
                {
                    return (AdvancedQueryRequiredError("$orderby"), HttpStatusCode.BadRequest);
                }
            }

            // $search on a directory object is its own advanced-query gate,
            // separate from the ne/not/endsWith/$orderby one above: it needs
            // only ConsistencyLevel: eventual, not $count=true too (the one
            // documented exception to "advanced query params" meaning both -
            // see https://learn.microsoft.com/graph/search-query-parameter).
            // On any other resource type $search isn't modeled at all by
            // this plugin - messages/people have their own, unrelated search
            // semantics (KQL properties, relevance scoring) this plugin
            // doesn't implement - so it's silently ignored there, unchanged
            // from this plugin's behavior before $search existed at all.
            var searchParam = query["$search"];
            SearchNode? searchNode = null;
            if (!string.IsNullOrWhiteSpace(searchParam) && isDirectoryObjectType)
            {
                if (!HasConsistencyLevelEventual(request))
                {
                    return (
                        ODataError("Request_UnsupportedQuery", "Request with $search query parameter only works through MSGraph with a special request header: 'ConsistencyLevel: eventual'"),
                        HttpStatusCode.BadRequest);
                }

                searchNode = TryParseSearch(searchParam);
                if (searchNode is null)
                {
                    return (ODataError("BadRequest", $"Unable to parse $search '{searchParam}'."), HttpStatusCode.BadRequest);
                }
            }

            IEnumerable<JsonObject> filtered = pool;
            if (filterNode is not null)
            {
                filtered = filtered.Where(filterNode.Evaluate);
            }

            // Real Graph ANDs $filter and $search together when both are
            // present (confirmed in the doc) - exactly what chaining a
            // second .Where() gives for free.
            if (searchNode is not null)
            {
                filtered = filtered.Where(searchNode.Evaluate);
            }

            if (orderByTerms is not null)
            {
                filtered = ApplyOrderBy(filtered, orderByTerms);
            }

            // Paging (msgraphProxy P0 #4). The total is taken before paging, so @odata.count
            // reports the whole result set, not just this page.
            var page = ApplyPaging(typeFullName, query, request, filtered);
            if (page.Error is not null)
            {
                return (page.Error, HttpStatusCode.BadRequest);
            }

            var selectedProps = GetSelectedProps(registry, typeFullName, request);
            var array = new JsonArray();
            foreach (var item in page.Items)
            {
                var trimmed = TrimTo(item, selectedProps);
                ApplyExpand(registry, trimmed, typeFullName, request);
                array.Add(trimmed);
            }

            var responseBody = new JsonObject { ["@odata.context"] = BuildODataContext(versionSegment, request), ["value"] = array };

            // $count=true on a non-directory-object resource is plain OData,
            // honored unconditionally; on a directory object it's gated the
            // same as everything else above - real Graph "silently ignores"
            // it rather than erroring (confirmed in the doc), so this just
            // omits @odata.count instead of rejecting the request.
            if (countTrue && (!isDirectoryObjectType || advancedQueryModeActive))
            {
                responseBody["@odata.count"] = page.Total;
            }

            if (page.NextLink is not null)
            {
                responseBody["@odata.nextLink"] = page.NextLink;
            }

            if (page.AppliedMaxPageSize is { } applied)
            {
                _pendingHeaders = [new("Preference-Applied", "odata.maxpagesize=" + applied.ToString(CultureInfo.InvariantCulture))];
            }

            return (responseBody, HttpStatusCode.OK);
        }

        if (method == "POST")
        {
            var created = BuildEntity(registry, typeFullName);
            var bodyObj = ParseBody(request);
            if (bodyObj is not null)
            {
                MergeInto(created, bodyObj);
            }

            if (!created.TryGetPropertyValue("id", out var idNode) || idNode is null)
            {
                created["id"] = MockAmbient.NewGuid("post:" + typeFullName).ToString();
            }

            pool.Add(created);
            var createdBody = created.DeepClone().AsObject();
            createdBody["@odata.context"] = BuildODataContext(versionSegment, request) + "/$entity";
            return (createdBody, HttpStatusCode.Created);
        }

        return (null, null);
    }

    private readonly record struct PageResult(List<JsonObject> Items, int Total, string? NextLink, int? AppliedMaxPageSize, JsonObject? Error);

    /// <summary>
    /// Cuts one page out of a filtered, ordered result set. Page size: $top, else the
    /// Prefer: odata.maxpagesize preference, else the type's default (the smaller of the two
    /// when both are given), capped at the type's maximum. $skiptoken is opaque to clients:
    /// base64url JSON carrying the offset and, in snapshot mode, the id of the result set
    /// frozen when page 1 was served.
    /// </summary>
    private PageResult ApplyPaging(string typeFullName, System.Collections.Specialized.NameValueCollection query, MockRequest request, IEnumerable<JsonObject> filtered)
    {
        var (defaultSize, maxSize) = _paging.SizesFor(typeFullName);

        int? top = null;
        if (query["$top"] is { } topParam)
        {
            if (!int.TryParse(topParam, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTop) || parsedTop < 1 || parsedTop > maxSize)
            {
                return new PageResult([], 0, null, null, ODataError(
                    "Request_BadRequest",
                    $"Invalid page size specified: '{topParam}'. Must be between 1 and {maxSize.ToString(CultureInfo.InvariantCulture)} inclusive."));
            }

            top = parsedTop;
        }

        var preferred = TryGetMaxPageSizePreference(request);
        int? applied = preferred is { } p ? Math.Clamp(p, 1, maxSize) : null;
        var pageSize = (top, applied) switch
        {
            ({ } t, { } a) => Math.Min(t, a),
            ({ } t, null) => t,
            (null, { } a) => a,
            _ => defaultSize,
        };

        List<JsonObject> results;
        var offset = 0;
        string? snapshotId = null;
        if (query["$skiptoken"] is { } skipToken)
        {
            if (!TryDecodeSkipToken(skipToken, out offset, out snapshotId))
            {
                return new PageResult([], 0, null, null, ODataError("Request_BadRequest", "The specified $skiptoken is invalid."));
            }

            if (snapshotId is not null)
            {
                if (!_snapshots.TryGetValue(snapshotId, out var frozen))
                {
                    return new PageResult([], 0, null, null, ODataError("Directory_ExpiredPageToken", "The specified page token value has expired and can no longer be included in your request."));
                }

                results = frozen;
            }
            else
            {
                results = [.. filtered];
            }
        }
        else
        {
            results = [.. filtered];
            if (_paging.Consistency == GraphMockPagingConsistency.Snapshot && results.Count > pageSize)
            {
                snapshotId = MockAmbient.NewGuid("snapshot").ToString("N");
                _snapshots[snapshotId] = results;
                _snapshotOrder.Enqueue(snapshotId);
                while (_snapshotOrder.Count > MaxSnapshots)
                {
                    _ = _snapshots.Remove(_snapshotOrder.Dequeue());
                }
            }
        }

        offset = Math.Min(offset, results.Count);
        var items = results.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + items.Count;
        var nextLink = nextOffset < results.Count ? BuildNextLink(request, EncodeSkipToken(nextOffset, snapshotId)) : null;

        return new PageResult(items, results.Count, nextLink, applied, null);
    }

    private static int? TryGetMaxPageSizePreference(MockRequest request)
    {
        if (request.GetHeader("Prefer") is not { } prefer)
        {
            return null;
        }

        const string prefix = "odata.maxpagesize=";
        foreach (var part in prefer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                return size;
            }
        }

        return null;
    }

    private static string EncodeSkipToken(int offset, string? snapshotId)
    {
        var json = new JsonObject { ["v"] = 1, ["o"] = offset, ["s"] = snapshotId }.ToJsonString();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeSkipToken(string token, out int offset, out string? snapshotId)
    {
        offset = 0;
        snapshotId = null;
        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            if (JsonNode.Parse(Convert.FromBase64String(base64)) is not JsonObject node ||
                node["o"] is not JsonValue o || !o.TryGetValue(out offset) || offset < 0)
            {
                return false;
            }

            snapshotId = node["s"] is JsonValue s && s.TryGetValue(out string? sid) ? sid : null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    // The nextLink keeps every query option of the current request ($select, $filter, $top, ...)
    // and replaces only $skiptoken, like Graph's own nextLinks.
    private static string BuildNextLink(MockRequest request, string skipToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        query.Remove("$skiptoken");
        var parts = new List<string>();
        foreach (var key in query.AllKeys)
        {
            if (key is null)
            {
                continue;
            }

            foreach (var value in query.GetValues(key) ?? [])
            {
                parts.Add(Uri.EscapeDataString(key).Replace("%24", "$", StringComparison.Ordinal) + "=" + Uri.EscapeDataString(value));
            }
        }

        parts.Add("$skiptoken=" + skipToken);
        return $"{request.RequestUri.Scheme}://{request.RequestUri.Authority}{request.RequestUri.AbsolutePath}?{string.Join('&', parts)}";
    }

    // Real Graph responses always carry "@odata.context" - collections at the
    // top level, single entities suffixed with "/$entity" - and callers do
    // rely on it: Maester's own Invoke-MtGraphRequest, for one, only stamps
    // "@odata.context" onto individual collection items when it's missing
    // from the *container* response (a latent bug there - it means to check
    // each item, not the container - that real Graph responses never trip
    // since they always carry it at the container level). Leaving it out
    // here made that dormant bug fire: a cached response's items would get
    // "@odata.context" added once, then a second read of that same cached
    // object would try to add it again and fail with "member already
    // exists". Matching real Graph's shape avoids that and is simply more
    // schema-accurate besides.
    private static string BuildODataContext(string versionSegment, MockRequest request)
    {
        var path = request.RequestUri.AbsolutePath;
        var versionIndex = path.IndexOf(versionSegment, StringComparison.OrdinalIgnoreCase);
        var remainder = versionIndex >= 0 ? path[(versionIndex + versionSegment.Length)..].Trim('/') : "";
        var firstSegment = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parenIndex = firstSegment.IndexOf('(', StringComparison.Ordinal);
        if (parenIndex >= 0)
        {
            firstSegment = firstSegment[..parenIndex];
        }

        return $"{request.RequestUri.Scheme}://{request.RequestUri.Host}{versionSegment}$metadata#{firstSegment}";
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleItem(SchemaRegistry registry, string versionSegment, string typeFullName, string itemId, string method, MockRequest request, QueryRestrictions? restrictions)
    {
        var pool = GetOrSeedPool(registry, typeFullName);
        var existing = pool.FindById(itemId);

        // unifiedRoleManagementAlert is unlike every other by-id entity here: its
        // ids aren't arbitrary keys of user-created resources, they're a small,
        // fixed set of well-known composite ids ("DirectoryRole_{tenantId}_{alertId}")
        // that Microsoft Graph always resolves for any real tenant, active or not -
        // confirmed directly against Maester's Test-MtPimAlertsExists, which GETs
        // exactly this id shape and, on a real tenant, always gets a 200 back (an
        // inactive alert, not a 404) even when nothing has ever triggered it. The
        // generic "404 unless it's already in the seeded pool" model below is right
        // for actual resource collections (users, groups, ...) but wrong here, so
        // this type materializes an inactive alert on first request instead of
        // 404ing - matching real Graph's behavior for a clean tenant, and letting
        // Maester's PIM alert checks (MT.1029-MT.1032) evaluate instead of erroring.
        if (existing is null && string.Equals(typeFullName, "microsoft.graph.unifiedRoleManagementAlert", StringComparison.Ordinal))
        {
            existing = BuildEntity(registry, typeFullName);
            existing["id"] = itemId;
            existing["isActive"] = false;
            pool.Add(existing);
        }

        if (existing is null)
        {
            return (ODataError("Request_ResourceNotFound", $"Resource '{itemId}' does not exist or one of its queried reference-property objects are not present."), HttpStatusCode.NotFound);
        }

        switch (method)
        {
            case "GET":
                {
                    var expandError = ValidateExpand(registry, typeFullName, request, restrictions);
                    if (expandError is not null)
                    {
                        return (expandError, HttpStatusCode.BadRequest);
                    }

                    var trimmed = TrimTo(existing, GetSelectedProps(registry, typeFullName, request));
                    ApplyExpand(registry, trimmed, typeFullName, request);
                    trimmed["@odata.context"] = BuildODataContext(versionSegment, request) + "/$entity";
                    return (trimmed, HttpStatusCode.OK);
                }
            case "DELETE":
                _ = pool.Remove(existing);
                return (null, HttpStatusCode.NoContent);
            case "PATCH":
            case "PUT":
                {
                    var bodyObj = ParseBody(request);
                    if (bodyObj is not null)
                    {
                        MergeInto(existing, bodyObj);
                        pool.Reindex();
                    }

                    return (existing.DeepClone(), HttpStatusCode.OK);
                }
            default:
                return (null, null);
        }
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleSingleton(SchemaRegistry registry, string versionSegment, string typeFullName, string method, MockRequest request, QueryRestrictions? restrictions)
    {
        if (method is not ("GET" or "PATCH" or "PUT"))
        {
            return (null, null);
        }

        var pool = GetOrSeedPool(registry, typeFullName);
        var record = pool[0];

        if (method is "PATCH" or "PUT")
        {
            var bodyObj = ParseBody(request);
            if (bodyObj is not null)
            {
                MergeInto(record, bodyObj);
            }
        }
        else
        {
            var expandError = ValidateExpand(registry, typeFullName, request, restrictions);
            if (expandError is not null)
            {
                return (expandError, HttpStatusCode.BadRequest);
            }
        }

        var trimmedRecord = TrimTo(record, GetSelectedProps(registry, typeFullName, request));
        ApplyExpand(registry, trimmedRecord, typeFullName, request);
        trimmedRecord["@odata.context"] = BuildODataContext(versionSegment, request) + "/$entity";
        return (trimmedRecord, HttpStatusCode.OK);
    }

    // $ref requests manage relationships between entities. This plugin models
    // each type as an independent pool of records rather than a real instance
    // graph, so it has no notion of which specific ids are linked to which -
    // it validates the request shape and reports the same success/failure a
    // real add/remove/set-reference call would, which is enough for callers
    // exercising that flow rather than asserting on the resulting membership.
    private static (JsonNode? Body, HttpStatusCode? StatusCode) HandleRef(ResolvedNode node, string method, MockRequest request)
    {
        if (node.ItemId is not null)
        {
            // .../{navProperty}/{id}/$ref - removing one member from a
            // collection, or clearing a to-one reference by id.
            return method == "DELETE" ? (null, HttpStatusCode.NoContent) : (null, null);
        }

        if (node.IsCollection)
        {
            // .../{navProperty}/$ref - adding a member to a collection.
            return method == "POST" ? HandleAddRef(request) : (null, null);
        }

        // .../{navProperty}/$ref on a to-one navigation property - setting or
        // clearing it outright.
        return method switch
        {
            "PUT" => HandleAddRef(request),
            "DELETE" => (null, HttpStatusCode.NoContent),
            _ => (null, null),
        };
    }

    private static (JsonNode? Body, HttpStatusCode? StatusCode) HandleAddRef(MockRequest request)
    {
        var bodyObj = ParseBody(request);
        if (bodyObj is null || !bodyObj.ContainsKey("@odata.id"))
        {
            return (ODataError("BadRequest", "An @odata.id property is required in the request body."), HttpStatusCode.BadRequest);
        }

        return (null, HttpStatusCode.NoContent);
    }

    private static JsonObject ODataError(string code, string message) =>
        new()
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

    // Bound Functions/Actions don't model their parameters, so request bodies and
    // parenthesized arguments are ignored - only the ReturnType shape is fabricated.
    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleFunction(SchemaRegistry registry, FunctionActionDef op, string method, MockRequest request)
    {
        if (method != "GET")
        {
            return (null, null);
        }

        return BuildOperationResult(registry, op.ReturnType);
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleAction(SchemaRegistry registry, FunctionActionDef op, string method, MockRequest request)
    {
        if (method != "POST")
        {
            return (null, null);
        }

        return BuildOperationResult(registry, op.ReturnType);
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) BuildOperationResult(SchemaRegistry registry, string? returnType)
    {
        if (returnType is null)
        {
            return (null, HttpStatusCode.NoContent);
        }

        // Functions returning entities (delta(), getAllSites(), ...) are served from the pools, so the
        // ids they return resolve on a follow-up GET, like real Graph.
        var elementType = StripCollection(returnType);
        if (!elementType.StartsWith("Edm.", StringComparison.Ordinal))
        {
            var entityType = ResolveRefName(registry, elementType);
            if (IsEntityLike(registry, entityType))
            {
                var pool = GetOrSeedPool(registry, entityType);
                return IsCollectionType(returnType)
                    ? (new JsonObject { ["value"] = new JsonArray([.. pool.Select(item => (JsonNode)item.DeepClone())]) }, HttpStatusCode.OK)
                    : (pool[0].DeepClone(), HttpStatusCode.OK);
            }
        }

        var value = BuildValue(registry, returnType, "value", 1);
        return IsCollectionType(returnType)
            ? (new JsonObject { ["value"] = value }, HttpStatusCode.OK)
            : (value as JsonObject ?? new JsonObject { ["value"] = value }, HttpStatusCode.OK);
    }

    // RawText, not Body, for the $value/$count text/plain shape - kept as a
    // separate field rather than stuffing a raw string into a JsonValue and
    // relying on JsonNode.ToString() to hand it back unquoted later, which
    // isn't a documented guarantee worth depending on.
    private readonly record struct MockResult(JsonNode? Body, HttpStatusCode? StatusCode, string? RawText = null, string? LogLabel = null, IReadOnlyList<KeyValuePair<string, string>>? ExtraHeaders = null);

    private MockResult HandleRawSegmentValue(SchemaRegistry registry, ResolvedNode node, string method, MockRequest request)
    {
        if (method != "GET")
        {
            // falls through unmocked, consistent with the (null, null) convention elsewhere
            return default;
        }

        // /$count as a URL segment on a directory object also needs
        // ConsistencyLevel: eventual - a different error shape than the
        // $filter-operator gate in HandleCollection (Request_BadRequest, not
        // Request_UnsupportedQuery), confirmed against the doc's own example.
        if (node.IsCount && AdvancedQueryDirectoryObjectTypes.Contains(node.TypeFullName) && !HasConsistencyLevelEventual(request))
        {
            return new MockResult(ODataError("Request_BadRequest", "$count is not currently supported."), HttpStatusCode.BadRequest, LogLabel: "$count without ConsistencyLevel");
        }

        var rawBody = node.IsCount
            ? GetOrSeedPool(registry, node.TypeFullName).Count.ToString(CultureInfo.InvariantCulture)
            : FakePrimitive(node.TypeFullName, node.PropName ?? "value")?.ToString() ?? "";

        return new MockResult(null, HttpStatusCode.OK, RawText: rawBody, LogLabel: node.IsCount ? "$count" : "$value");
    }

    // Shared by the top-level dispatch in BeforeRequestAsync and by each
    // $batch sub-request (see HandleBatch) - resolving a (method, path)
    // pair against the schema and producing a result is identical either
    // way, only how the caller learned about the request and what it does
    // with the result differs. Returns null specifically for "nothing in the
    // schema matches this path at all" (distinct from a MockResult with a
    // null StatusCode, which means "resolved to a real resource, but this
    // HTTP method isn't supported for that resource shape") - callers treat
    // the two differently (only the former logs "No schema match").
    private MockResult? ResolveAndProcess(SchemaRegistry registry, string versionSegment, string method, string absolutePath, MockRequest request)
    {
        var versionIndex = absolutePath.IndexOf(versionSegment, StringComparison.OrdinalIgnoreCase);
        if (versionIndex < 0)
        {
            return null;
        }

        var remainder = absolutePath[(versionIndex + versionSegment.Length)..].Trim('/');
        if (remainder.Length == 0)
        {
            return null;
        }

        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resolved = ResolvePath(registry, segments);
        if (resolved is null)
        {
            return null;
        }

        var node = resolved.Value;
        if (node.IsValue || node.IsCount)
        {
            return HandleRawSegmentValue(registry, node, method, request);
        }

        var restrictions = GetRestrictions(registry, node.SetOrSingletonName, node.TypeFullName);
        _pendingHeaders = null;
        var (responseBody, statusCode) = node.IsRef
            ? HandleRef(node, method, request)
            : node.Operation is not null
                ? (node.Operation.IsAction ? HandleAction(registry, node.Operation, method, request) : HandleFunction(registry, node.Operation, method, request))
                : node.IsCollection
                    ? HandleCollection(registry, versionSegment, node.TypeFullName, method, request, restrictions)
                    : node.ItemId is not null
                        ? HandleItem(registry, versionSegment, node.TypeFullName, node.ItemId, method, request, restrictions)
                        : HandleSingleton(registry, versionSegment, node.TypeFullName, method, request, restrictions);

        var extraHeaders = _pendingHeaders;
        _pendingHeaders = null;
        return new MockResult(responseBody, statusCode, LogLabel: node.TypeFullName, ExtraHeaders: extraHeaders);
    }

    private static MockResponse ToResponse(MockResult result, MockRequest request)
    {
        var status = (int)result.StatusCode!.Value;
        if (result.RawText is not null)
        {
            return new MockResponse(status, [new("Content-Type", "text/plain")], result.RawText, result.LogLabel);
        }

        // Mirrors Dev Proxy v3's ProxyUtils.BuildGraphResponseHeaders, so the adapter's output is unchanged.
        var requestId = MockAmbient.NewGuid("request").ToString();
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Cache-Control", "no-store"),
            new("x-ms-ags-diagnostic", ""),
            new("Strict-Transport-Security", ""),
            new("request-id", requestId),
            new("client-request-id", requestId),
            new("Date", MockAmbient.UtcNow.ToString("r", CultureInfo.InvariantCulture)),
            new("Content-Type", "application/json"),
        };
        if (result.ExtraHeaders is not null)
        {
            headers.AddRange(result.ExtraHeaders);
        }

        if (request.GetHeader("Origin") is not null)
        {
            headers.Add(new("Access-Control-Allow-Origin", "*"));
            headers.Add(new("Access-Control-Expose-Headers", "ETag, Location, Preference-Applied, Content-Range, request-id, client-request-id, ReadWriteConsistencyToken, SdkVersion, WWW-Authenticate, x-ms-client-gcc-tenant, Retry-After"));
        }

        return new MockResponse(status, headers, result.Body?.ToJsonString() ?? string.Empty, result.LogLabel);
    }

    private static List<string> GetSelectedProps(SchemaRegistry registry, string typeFullName, MockRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var selectParam = query["$select"];
        return selectParam is not null ? SplitSelect(selectParam) : DefaultProps(registry, typeFullName);
    }

    private EntityPool GetOrSeedPool(SchemaRegistry registry, string typeFullName)
    {
        if (_store.TryGetValue(typeFullName, out var pool))
        {
            return pool;
        }

        var seeded = string.Equals(typeFullName, "microsoft.graph.subscribedSku", StringComparison.Ordinal) && _subscribedSkus.Count > 0
            ? SeedSubscribedSkuPool(registry, _subscribedSkus)
            : SeedGenericPool(registry, typeFullName, _determinism.PoolSizeFor(typeFullName));

        // Name-based ids: with a seed, record i of type T is always uuid(seed, T, i), regardless of
        // which types were touched first. subscribedSku ids are composite and keep their own scheme.
        if (_entropy.IsSeeded && !string.Equals(typeFullName, "microsoft.graph.subscribedSku", StringComparison.Ordinal))
        {
            for (var i = 0; i < seeded.Count; i++)
            {
                if (seeded[i].ContainsKey("id"))
                {
                    seeded[i]["id"] = _entropy.Named("id:" + typeFullName + "#" + i.ToString(CultureInfo.InvariantCulture)).ToString();
                }
            }
        }

        var created = new EntityPool(seeded);
        _store[typeFullName] = created;
        return created;
    }

    private static List<JsonObject> SeedGenericPool(SchemaRegistry registry, string typeFullName, int count)
    {
        var first = BuildEntity(registry, typeFullName);
        var pool = new List<JsonObject>(count) { first };
        if (count >= 2)
        {
            pool.Add(SecondSample(first, typeFullName));
        }

        for (var i = 2; i < count; i++)
        {
            pool.Add(NthSample(first, typeFullName, i));
        }

        return pool;
    }

    // Records 3..N for larger pools: same shape as the first, distinct id and names.
    private static JsonObject NthSample(JsonObject source, string fullName, int index)
    {
        var clone = source.DeepClone().AsObject();
        var n = (index + 1).ToString(CultureInfo.InvariantCulture);
        if (clone.ContainsKey("id"))
        {
            clone["id"] = MockAmbient.NewGuid("prop:id").ToString();
        }

        if (string.Equals(fullName, "microsoft.graph.user", StringComparison.Ordinal))
        {
            if (clone.ContainsKey("displayName")) clone["displayName"] = "User " + n;
            if (clone.ContainsKey("givenName")) clone["givenName"] = "User";
            if (clone.ContainsKey("surname")) clone["surname"] = n;
            if (clone.ContainsKey("userPrincipalName")) clone["userPrincipalName"] = "user" + n + "@contoso.com";
            if (clone.ContainsKey("mail")) clone["mail"] = "user" + n + "@contoso.com";
            if (clone.ContainsKey("mailNickname")) clone["mailNickname"] = "user" + n;
            return clone;
        }

        foreach (var key in new[] { "displayName", "name" })
        {
            if (clone[key] is JsonValue value && value.TryGetValue(out string? text))
            {
                clone[key] = text + " " + n;
            }
        }

        return clone;
    }

    // Real Graph shares one accountId/accountName across every subscribedSku
    // in a tenant, and derives each sku's own "id" as "{accountId}_{skuId}"
    // rather than a bare guid - both confirmed against a real tenant's
    // subscribedSkus response.
    private static List<JsonObject> SeedSubscribedSkuPool(SchemaRegistry registry, IEnumerable<GraphSchemaMockSubscribedSkuConfiguration> skus)
    {
        var accountId = MockAmbient.NewGuid("sku:account").ToString();
        const string accountName = "contoso";
        return [.. skus.Select(sku => BuildSubscribedSku(registry, sku, accountId, accountName))];
    }

    private static JsonObject BuildSubscribedSku(SchemaRegistry registry, GraphSchemaMockSubscribedSkuConfiguration sku, string accountId, string accountName)
    {
        var entity = BuildEntity(registry, "microsoft.graph.subscribedSku");
        var skuId = MockAmbient.NewGuid("sku:id").ToString();
        entity["id"] = $"{accountId}_{skuId}";
        entity["accountId"] = accountId;
        entity["accountName"] = accountName;
        entity["appliesTo"] = "User";
        entity["skuId"] = skuId;
        entity["skuPartNumber"] = sku.SkuPartNumber;
        entity["capabilityStatus"] = sku.CapabilityStatus;
        entity["consumedUnits"] = sku.ConsumedUnits;
        entity["prepaidUnits"] = new JsonObject
        {
            ["enabled"] = sku.PrepaidUnitsEnabled,
            ["suspended"] = 0,
            ["warning"] = 0,
            ["lockedOut"] = 0,
        };
        entity["subscriptionIds"] = new JsonArray(MockAmbient.NewGuid("sku:subscription").ToString());

        var servicePlans = new JsonArray();
        foreach (var plan in sku.ServicePlans)
        {
            servicePlans.Add(new JsonObject
            {
                ["servicePlanId"] = plan.ServicePlanId,
                ["servicePlanName"] = plan.ServicePlanName ?? plan.ServicePlanId,
                ["provisioningStatus"] = plan.ProvisioningStatus ?? "Success",
                ["appliesTo"] = "User",
            });
        }

        entity["servicePlans"] = servicePlans;
        return entity;
    }

    private static JsonObject? ParseBody(MockRequest request)
    {
        var bodyString = request.Body;
        if (string.IsNullOrWhiteSpace(bodyString))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(bodyString)?.AsObject();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void MergeInto(JsonObject target, JsonObject overrides)
    {
        foreach (var kvp in overrides)
        {
            target[kvp.Key] = kvp.Value?.DeepClone();
        }
    }

    // --- Path resolution ---

    private static ResolvedNode? ResolvePath(SchemaRegistry registry, string[] segments)
    {
        ResolvedNode current;
        if (registry.EntitySets.TryGetValue(segments[0], out var entitySetType))
        {
            current = new ResolvedNode(ResolveRefName(registry, entitySetType), true, null, SetOrSingletonName: segments[0]);
        }
        else if (registry.Singletons.TryGetValue(segments[0], out var singletonType))
        {
            current = new ResolvedNode(ResolveRefName(registry, singletonType), false, null, SetOrSingletonName: segments[0]);
        }
        else
        {
            return null;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;
            if (segment.StartsWith('$'))
            {
                if (string.Equals(segment, "$ref", StringComparison.OrdinalIgnoreCase) && isLast)
                {
                    // .../{navProperty}/$ref or .../{navProperty}/{id}/$ref - a
                    // relationship request against whatever navigation property
                    // was just resolved, rather than the entity itself.
                    return current with { IsRef = true };
                }

                if (string.Equals(segment, "$count", StringComparison.OrdinalIgnoreCase) && isLast && current.IsCollection)
                {
                    return current with { IsCount = true };
                }

                // $value on its own (not right after a property), $top, etc. -
                // not supported, let the request fall through.
                return null;
            }

            if (current.IsCollection)
            {
                // A collection-bound Function/Action (e.g. GET /applications/delta())
                // always terminates the path - try it before assuming the segment is
                // an item id, which is what every such segment resolved to before.
                var collectionOp = TryMatchBoundOperation(registry, current, segment);
                if (collectionOp is not null)
                {
                    return isLast ? current with { Operation = collectionOp } : null;
                }

                // a segment right after a collection is always an item id - `with`
                // (not a fresh ResolvedNode) so SetOrSingletonName survives: a
                // restriction against the "users" entity set still applies to
                // GET /users/{id}, not just the bare collection.
                current = current with { IsCollection = false, ItemId = segment };
                continue;
            }

            (string Name, string Type)? navMatch = null;
            foreach (var nav in GetAllNavigationProperties(registry, current.TypeFullName))
            {
                if (string.Equals(nav.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    navMatch = nav;
                    break;
                }
            }

            if (navMatch is null)
            {
                // Try a singleton/item-bound Function/Action (e.g. .../{id}/checkMemberGroups)
                // only after a real navigation property, so an existing nav-property route can
                // never be shadowed by a same-named Function/Action.
                var itemOp = TryMatchBoundOperation(registry, current, segment);
                if (itemOp is not null)
                {
                    return isLast ? current with { Operation = itemOp } : null;
                }

                // .../{property}/$value - the raw value of a scalar/stream property.
                if (i == segments.Length - 2 && string.Equals(segments[i + 1], "$value", StringComparison.OrdinalIgnoreCase))
                {
                    var propMatch = GetAllProperties(registry, current.TypeFullName)
                        .FirstOrDefault(p => string.Equals(p.Name, segment, StringComparison.OrdinalIgnoreCase) && p.Type.StartsWith("Edm.", StringComparison.Ordinal));
                    if (propMatch.Name is not null)
                    {
                        return current with { IsValue = true, TypeFullName = propMatch.Type, PropName = propMatch.Name };
                    }
                }

                return null;
            }

            current = new ResolvedNode(ResolveRefName(registry, StripCollection(navMatch.Value.Type)), IsCollectionType(navMatch.Value.Type), null);
        }

        return current;
    }

    private static string StripCollection(string type) =>
        type.StartsWith("Collection(", StringComparison.Ordinal) && type.EndsWith(')')
            ? type[11..^1]
            : type;

    private static bool IsCollectionType(string type) =>
        type.StartsWith("Collection(", StringComparison.Ordinal);

    private static string ResolveRefName(SchemaRegistry registry, string rawRef)
    {
        foreach (var pair in registry.AliasMap)
        {
            if (rawRef.StartsWith(pair.Key + ".", StringComparison.Ordinal))
            {
                return pair.Value + "." + rawRef[(pair.Key.Length + 1)..];
            }
        }

        return rawRef;
    }

    // --- Value generation ---

    private static JsonObject BuildEntity(SchemaRegistry registry, string fullName) => BuildObject(registry, fullName, 1);

    private static JsonObject BuildObject(SchemaRegistry registry, string fullName, int depth)
    {
        var obj = new JsonObject();
        foreach (var (name, type) in GetAllProperties(registry, fullName))
        {
            obj[name] = BuildValue(registry, type, name, depth);
        }

        return obj;
    }

    private static JsonNode? BuildValue(SchemaRegistry registry, string type, string propName, int depth)
    {
        if (type.StartsWith("Collection(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            var item = BuildValue(registry, type[11..^1], propName, depth);
            var array = new JsonArray();
            if (item is not null)
            {
                array.Add(item);
            }

            return array;
        }

        if (type.StartsWith("Edm.", StringComparison.Ordinal))
        {
            return FakePrimitive(type, propName);
        }

        var fullName = ResolveRefName(registry, type);

        if (registry.EnumTypes.TryGetValue(fullName, out var members))
        {
            return members.Count > 0 ? members[0] : "unknown";
        }

        if (registry.Types.ContainsKey(fullName))
        {
            return depth >= MaxDepth ? new JsonObject() : BuildObject(registry, fullName, depth + 1);
        }

        // Unresolved (rare cross-namespace edge case): best-effort string.
        return $"Sample {propName}";
    }

    private static JsonNode? FakePrimitive(string edmType, string propName)
    {
        if (NameFakes.TryGetValue(propName, out var fake))
        {
            return fake();
        }

        return edmType switch
        {
            "Edm.String" => $"Sample {propName}",
            "Edm.Boolean" => true,
            "Edm.Int16" or "Edm.Int32" or "Edm.Int64" or "Edm.Byte" or "Edm.SByte" => 1,
            "Edm.Double" or "Edm.Single" or "Edm.Decimal" => 1.0,
            "Edm.Guid" => MockAmbient.NewGuid("prop:" + propName).ToString(),
            "Edm.DateTimeOffset" => MockAmbient.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            "Edm.Date" => MockAmbient.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Edm.TimeOfDay" => "12:00:00.0000000",
            "Edm.Duration" => "PT1H",
            "Edm.Binary" => "U2FtcGxlIGJpbmFyeSBkYXRh",
            "Edm.Stream" => null,
            _ => $"Sample {propName}",
        };
    }

    private static JsonObject SecondSample(JsonObject source, string fullName)
    {
        var clone = source.DeepClone().AsObject();
        if (clone.ContainsKey("id"))
        {
            clone["id"] = MockAmbient.NewGuid("prop:id").ToString();
        }

        if (string.Equals(fullName, "microsoft.graph.user", StringComparison.Ordinal))
        {
            if (clone.ContainsKey("displayName")) clone["displayName"] = "Jane Doe";
            if (clone.ContainsKey("givenName")) clone["givenName"] = "Jane";
            if (clone.ContainsKey("surname")) clone["surname"] = "Doe";
            if (clone.ContainsKey("userPrincipalName")) clone["userPrincipalName"] = "jane.doe@contoso.com";
            if (clone.ContainsKey("mail")) clone["mail"] = "jane.doe@contoso.com";
            if (clone.ContainsKey("mailNickname")) clone["mailNickname"] = "janedoe";
        }
        else
        {
            if (clone["displayName"] is JsonValue dn && dn.TryGetValue(out string? dnStr))
            {
                clone["displayName"] = $"{dnStr} 2";
            }

            if (clone["name"] is JsonValue nm && nm.TryGetValue(out string? nmStr))
            {
                clone["name"] = $"{nmStr} 2";
            }
        }

        return clone;
    }

    // --- $select / default field trimming ---

    private static List<string> SplitSelect(string select) =>
        [.. select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<string> DefaultProps(SchemaRegistry registry, string fullName)
    {
        if (string.Equals(fullName, "microsoft.graph.user", StringComparison.Ordinal))
        {
            return [.. UserDefaultProperties];
        }

        // servicePlans is a Collection(complexType), so the generic
        // scalar-only trimming below would otherwise drop it by default -
        // and real Graph callers (e.g. Maester's license checks) query
        // subscribedSkus with no $select at all, relying on it being there.
        if (string.Equals(fullName, "microsoft.graph.subscribedSku", StringComparison.Ordinal))
        {
            return [.. SubscribedSkuDefaultProperties];
        }

        // GetAllProperties never includes navigation properties (those live in a
        // separate NavigationProperties list per TypeDef) - real Graph's own
        // default-response behavior (no $select) is "every direct property,
        // whatever its kind, minus expanded navigation properties", not
        // "primitives only". An earlier version of this filtered out anything
        // whose EDM type wasn't Edm.* (dropping complex- and enum-typed
        // properties), which is why conditionalAccessPolicy - real properties
        // are almost entirely complex-typed (conditions, grantControls,
        // sessionControls) or enum-typed (state) - came back as just
        // id/description/displayName/created-/modifiedDateTime with nothing a
        // caller could actually act on (confirmed directly: this is what made
        // Maester's Test-MtCaMisconfiguredIDProtection/Test-MtCaAzureDevOps
        // throw instead of evaluate, since `$policy.conditions` and `.state`
        // were always absent). The subscribedSku special case above exists for
        // the same underlying reason (its real default list needs 11 properties,
        // over the Take(10) cap below) and stays for that reason even though
        // this fix covers its complex-property-dropping half too.
        var otherNames = GetAllProperties(registry, fullName)
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, "id", StringComparison.Ordinal))
            .ToList();

        var result = new List<string> { "id" };
        result.AddRange(otherNames.Take(10));
        return result;
    }

    private static JsonObject TrimTo(JsonObject source, IEnumerable<string> propNames)
    {
        var result = new JsonObject();
        foreach (var name in propNames)
        {
            if (source.TryGetPropertyValue(name, out var value))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    // --- $expand ---

    // Checked once up front (not per-item inside ApplyExpand's loop, which runs once
    // per row in a collection response) since the answer never varies per-item -
    // only the requested $expand param and the resource's own restrictions matter.
    // A restricted-but-otherwise-known nav property name is a real 400 in the real
    // API (e.g. "Expand not supported for property 'chats'" on /users) - unlike an
    // unrecognized name entirely, which ApplyExpand still silently skips, since that
    // case was never about a genuine restriction.
    private static JsonObject? ValidateExpand(SchemaRegistry registry, string typeFullName, MockRequest request, QueryRestrictions? restrictions)
    {
        if (restrictions is null)
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var expandParam = query["$expand"];
        if (string.IsNullOrWhiteSpace(expandParam))
        {
            return null;
        }

        if (!restrictions.Expandable)
        {
            return ODataError("BadRequest", "$expand is not supported for this resource.");
        }

        if (restrictions.NonExpandableProperties.Count == 0)
        {
            return null;
        }

        var navProps = GetAllNavigationProperties(registry, typeFullName);
        foreach (var raw in expandParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parenIdx = raw.IndexOf('(', StringComparison.Ordinal);
            var name = parenIdx >= 0 ? raw[..parenIdx] : raw;

            foreach (var nav in navProps)
            {
                if (string.Equals(nav.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    restrictions.NonExpandableProperties.Contains(nav.Name))
                {
                    return ODataError("BadRequest", $"Expand not supported for property '{nav.Name}'.");
                }
            }
        }

        return null;
    }

    // Reuses BuildValue - the same recursive fabrication already used for nested
    // complex-type properties - rather than a parallel path, so expanded nav
    // properties get the same MaxDepth-capped, schema-accurate shape. Must run
    // after TrimTo, mutating the trimmed object: otherwise an expanded property
    // absent from $select/DefaultProps would immediately be stripped back out,
    // whereas real Graph always surfaces an expanded property regardless of $select.
    private void ApplyExpand(SchemaRegistry registry, JsonObject target, string typeFullName, MockRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var expandParam = query["$expand"];
        if (string.IsNullOrWhiteSpace(expandParam))
        {
            return;
        }

        var navProps = GetAllNavigationProperties(registry, typeFullName);
        foreach (var raw in expandParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Nested options, e.g. "manager($select=id,displayName)", aren't
            // supported - only the bare nav property name before "(" is used.
            var parenIdx = raw.IndexOf('(', StringComparison.Ordinal);
            var name = parenIdx >= 0 ? raw[..parenIdx] : raw;

            (string Name, string Type)? match = null;
            foreach (var nav in navProps)
            {
                if (string.Equals(nav.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    match = nav;
                    break;
                }
            }

            // Unknown $expand name: silently skipped rather than a 400, since this
            // is purely additive response enrichment - unlike $filter, getting it
            // wrong can't change which records match.
            if (match is not null)
            {
                target[match.Value.Name] = ExpandFromPool(registry, target, match.Value.Name, match.Value.Type)
                    ?? BuildValue(registry, match.Value.Type, match.Value.Name, 1);
            }
        }
    }

    // Expanded entities come from the target type's pool (cloned), so an expanded manager or
    // member is a real, GET-able record. A to-one pick is stable per (parent id, navigation).
    // Per-parent relationship data is P1 #9/#10; until then every parent sees the same members.
    private const int MaxExpandedItems = 20;

    private JsonNode? ExpandFromPool(SchemaRegistry registry, JsonObject target, string navName, string navType)
    {
        var entityType = ResolveRefName(registry, StripCollection(navType));
        if (!IsEntityLike(registry, entityType))
        {
            return null;
        }

        var pool = GetOrSeedPool(registry, entityType);
        if (IsCollectionType(navType))
        {
            return new JsonArray([.. pool.Take(MaxExpandedItems).Select(item => (JsonNode)item.DeepClone())]);
        }

        var parentId = target["id"] is JsonValue idValue && idValue.TryGetValue(out string? id) ? id : "";
        var index = (int)(StableHash(parentId + "/" + navName) % (uint)pool.Count);
        if (pool.Count > 1 && pool[index]["id"] is JsonValue picked && picked.TryGetValue(out string? pickedId) && pickedId == parentId)
        {
            index = (index + 1) % pool.Count; // a user is not their own manager
        }

        return pool[index].DeepClone();
    }

    // string.GetHashCode is randomized per process; this must be stable across runs.
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    private static bool IsEntityLike(SchemaRegistry registry, string fullName) =>
        registry.Types.ContainsKey(fullName) &&
        GetAllProperties(registry, fullName).Exists(p => string.Equals(p.Name, "id", StringComparison.Ordinal));

    // --- $orderby ---

    private sealed record OrderByTerm(string PropName, bool Descending);

    // Supports "prop", "prop asc" and "prop desc", comma-separated - no nested
    // property paths, same scope limitation as $filter above. Returns null on any
    // malformed term, which the caller turns into a 400 rather than silently
    // ignoring the sort.
    private static List<OrderByTerm>? TryParseOrderBy(string orderBy)
    {
        var terms = new List<OrderByTerm>();
        foreach (var raw in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (parts.Length)
            {
                case 1:
                    terms.Add(new OrderByTerm(parts[0], Descending: false));
                    break;
                case 2 when string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase):
                    terms.Add(new OrderByTerm(parts[0], Descending: true));
                    break;
                case 2 when string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase):
                    terms.Add(new OrderByTerm(parts[0], Descending: false));
                    break;
                default:
                    return null;
            }
        }

        return terms.Count > 0 ? terms : null;
    }

    // A stable multi-key sort (OrderBy/ThenBy, not List.Sort) so items that tie on
    // every requested property keep their original relative order, same as real
    // Graph's own $orderby behavior.
    private static IEnumerable<JsonObject> ApplyOrderBy(IEnumerable<JsonObject> source, List<OrderByTerm> terms)
    {
        var comparer = Comparer<string>.Create(CompareStrings);
        IOrderedEnumerable<JsonObject>? ordered = null;
        foreach (var term in terms)
        {
            string KeySelector(JsonObject item) => FindPropertyValue(item, term.PropName)?.ToString() ?? "";
            ordered = ordered is null
                ? term.Descending ? source.OrderByDescending(KeySelector, comparer) : source.OrderBy(KeySelector, comparer)
                : term.Descending ? ordered.ThenByDescending(KeySelector, comparer) : ordered.ThenBy(KeySelector, comparer);
        }

        return ordered ?? source;
    }

    // --- $filter ---

    // Evaluated generically against whatever properties the plugin already
    // fabricates, not Graph's real per-property $filter restrictions - this is a
    // mock, not the real API, so replicating those restrictions isn't the point.
    private abstract record FilterNode
    {
        public abstract bool Evaluate(JsonObject item);

        // Feeds the restriction check in HandleCollection: real Graph rejects
        // $filter on properties FilterRestrictions marks non-filterable, so the
        // set of property names an expression actually touches has to be known
        // before evaluating it against the pool.
        public abstract void CollectPropertyNames(HashSet<string> names);

        // Feeds the advanced-query gate in HandleCollection: on directory
        // objects, ne/not/endsWith anywhere in the expression require
        // ConsistencyLevel: eventual + $count=true - see
        // https://learn.microsoft.com/graph/aad-advanced-queries. "in" is
        // deliberately not gated: the doc states it's supported by default
        // wherever "eq" is.
        public abstract void CollectAdvancedOperators(List<string> operators);
    }

    private sealed record NotNode(FilterNode Inner) : FilterNode
    {
        public override bool Evaluate(JsonObject item) => !Inner.Evaluate(item);
        public override void CollectPropertyNames(HashSet<string> names) => Inner.CollectPropertyNames(names);

        public override void CollectAdvancedOperators(List<string> operators)
        {
            operators.Add("not");
            Inner.CollectAdvancedOperators(operators);
        }
    }

    private sealed record LogicalNode(FilterNode Left, FilterNode Right, bool IsAnd) : FilterNode
    {
        public override bool Evaluate(JsonObject item) =>
            IsAnd ? Left.Evaluate(item) && Right.Evaluate(item) : Left.Evaluate(item) || Right.Evaluate(item);

        public override void CollectPropertyNames(HashSet<string> names)
        {
            Left.CollectPropertyNames(names);
            Right.CollectPropertyNames(names);
        }

        public override void CollectAdvancedOperators(List<string> operators)
        {
            Left.CollectAdvancedOperators(operators);
            Right.CollectAdvancedOperators(operators);
        }
    }

    // Distinguishes the unquoted `null` literal (a real null check) from the
    // quoted string `'null'` (an ordinary string comparison) - the tokenizer
    // already strips quotes before a literal reaches a FilterNode, so this has
    // to be captured at parse time, before UnwrapFilterLiteral runs.
    private readonly record struct FilterLiteral(string Value, bool IsNull);

    private sealed record ComparisonNode(string PropName, string Op, FilterLiteral Literal) : FilterNode
    {
        public override bool Evaluate(JsonObject item)
        {
            var value = FindPropertyValue(item, PropName);

            // "prop eq null" / "prop ne null" - real Graph's documented pattern
            // for checking whether a property is unset (e.g. "companyName ne
            // null"). A missing property and a property whose JSON value is
            // itself null are indistinguishable once resolved through
            // FindPropertyValue - both correctly count as "is null" here.
            if (Literal.IsNull && Op is "eq" or "ne")
            {
                return Op == "eq" ? value is null : value is not null;
            }

            if (value is null)
            {
                return false;
            }

            var cmp = CompareStrings(value.ToString(), Literal.Value);
            return Op switch
            {
                "eq" => cmp == 0,
                "ne" => cmp != 0,
                "gt" => cmp > 0,
                "lt" => cmp < 0,
                "ge" => cmp >= 0,
                "le" => cmp <= 0,
                _ => false,
            };
        }

        public override void CollectPropertyNames(HashSet<string> names) => names.Add(PropName);

        public override void CollectAdvancedOperators(List<string> operators)
        {
            if (Op == "ne")
            {
                operators.Add("ne");
            }
        }
    }

    // "prop in (lit1, lit2, ...)" - equivalent to an OR-chain of eq comparisons,
    // which is exactly how it's evaluated; a separate node only because the
    // syntax (a parenthesized literal list, not a single right-hand operand)
    // doesn't fit ComparisonNode's shape.
    private sealed record InNode(string PropName, List<FilterLiteral> Literals) : FilterNode
    {
        public override bool Evaluate(JsonObject item)
        {
            var value = FindPropertyValue(item, PropName);
            foreach (var literal in Literals)
            {
                if (literal.IsNull ? value is null : value is not null && CompareStrings(value.ToString(), literal.Value) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public override void CollectPropertyNames(HashSet<string> names) => names.Add(PropName);

        // Not gated: "in" works by default wherever "eq" does, per the doc.
        public override void CollectAdvancedOperators(List<string> operators)
        {
        }
    }

    private sealed record FunctionCallNode(string FuncName, string PropName, string Literal) : FilterNode
    {
        public override bool Evaluate(JsonObject item)
        {
            var value = FindPropertyValue(item, PropName);
            if (value is null)
            {
                return false;
            }

            var str = value.ToString();
            return FuncName switch
            {
                "startswith" => str.StartsWith(Literal, StringComparison.Ordinal),
                "contains" => str.Contains(Literal, StringComparison.Ordinal),
                "endswith" => str.EndsWith(Literal, StringComparison.Ordinal),
                _ => false,
            };
        }

        public override void CollectPropertyNames(HashSet<string> names) => names.Add(PropName);

        public override void CollectAdvancedOperators(List<string> operators)
        {
            if (FuncName == "endswith")
            {
                operators.Add("endsWith");
            }
        }
    }

    // Shared by $filter's ComparisonNode/InNode and $orderby's sort comparer -
    // date- and number-aware where possible, falling back to ordinal string
    // comparison (matches this plugin's existing $filter semantics, not
    // attempting real OData collation).
    private static int CompareStrings(string left, string right)
    {
        if (DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.None, out var leftDate) &&
            DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.None, out var rightDate))
        {
            return leftDate.CompareTo(rightDate);
        }

        if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNum) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNum))
        {
            return leftNum.CompareTo(rightNum);
        }

        return string.CompareOrdinal(left, right);
    }

    // Walks a '/'-delimited path (e.g. "from/emailAddress/address",
    // "passwordProfile/forceChangePasswordNextSignIn") through nested complex-
    // type properties - which this plugin's BuildObject already fabricates in
    // full, since they're structural Properties, not NavigationProperties. A
    // path segment that resolves to a navigation property (e.g. "manager/...")
    // still can't be walked, though: navigation properties are never part of
    // the underlying pool item, only added on demand by $expand onto a
    // *trimmed copy* $filter never sees - that's an inherent limit of this
    // plugin's one-pool-per-type model, not something a smarter path walk
    // could work around.
    private static JsonValue? FindPropertyValue(JsonObject item, string propPath)
    {
        var segments = propPath.Split('/');
        var current = item;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (FindChild(current, segments[i]) is not JsonObject next)
            {
                return null;
            }

            current = next;
        }

        return FindChild(current, segments[^1]) as JsonValue;
    }

    private static JsonNode? FindChild(JsonObject obj, string name)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Missing property: not an error, just never matches - consistent with
        // TrimTo already silently skipping properties it can't find.
        return null;
    }

    private static readonly HashSet<string> FilterComparisonOps = new(StringComparer.OrdinalIgnoreCase) { "eq", "ne", "gt", "lt", "ge", "le" };
    private static readonly HashSet<string> FilterFunctionNames = new(StringComparer.OrdinalIgnoreCase) { "startswith", "contains", "endswith" };
    private static readonly Regex FilterTokenRegex = new(@"'(?:[^']|'')*'|[()]|,|[^\s()',]+", RegexOptions.None, TimeSpan.FromSeconds(5));

    // Supports eq/ne/gt/lt/ge/le, and/or/not, in, startswith/contains/endswith,
    // and '/'-separated paths through nested complex-type properties. Still out
    // of scope: the any/all lambda operators, has, and the $count-on-collection
    // pseudo-property - all explicitly advanced-query-gated or rare enough in
    // real Graph usage that they're a separate, larger piece of work rather
    // than folded in here. Returns null on any parse failure, which the caller
    // turns into a 400 rather than silently ignoring the filter.
    private static FilterNode? TryParseFilter(string filter)
    {
        var tokens = FilterTokenRegex.Matches(filter).Select(m => m.Value).ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        var pos = 0;
        var node = ParseFilterOr(tokens, ref pos);
        return node is not null && pos == tokens.Count ? node : null;
    }

    private static FilterNode? ParseFilterOr(List<string> tokens, ref int pos)
    {
        var left = ParseFilterAnd(tokens, ref pos);
        while (left is not null && pos < tokens.Count && string.Equals(tokens[pos], "or", StringComparison.OrdinalIgnoreCase))
        {
            pos++;
            var right = ParseFilterAnd(tokens, ref pos);
            left = right is null ? null : new LogicalNode(left, right, IsAnd: false);
        }

        return left;
    }

    private static FilterNode? ParseFilterAnd(List<string> tokens, ref int pos)
    {
        var left = ParseFilterNot(tokens, ref pos);
        while (left is not null && pos < tokens.Count && string.Equals(tokens[pos], "and", StringComparison.OrdinalIgnoreCase))
        {
            pos++;
            var right = ParseFilterNot(tokens, ref pos);
            left = right is null ? null : new LogicalNode(left, right, IsAnd: true);
        }

        return left;
    }

    private static FilterNode? ParseFilterNot(List<string> tokens, ref int pos)
    {
        if (pos < tokens.Count && string.Equals(tokens[pos], "not", StringComparison.OrdinalIgnoreCase))
        {
            pos++;
            var inner = ParseFilterNot(tokens, ref pos);
            return inner is null ? null : new NotNode(inner);
        }

        return ParseFilterPrimary(tokens, ref pos);
    }

    private static FilterNode? ParseFilterPrimary(List<string> tokens, ref int pos)
    {
        if (pos >= tokens.Count)
        {
            return null;
        }

        if (tokens[pos] == "(")
        {
            pos++;
            var inner = ParseFilterOr(tokens, ref pos);
            if (inner is null || pos >= tokens.Count || tokens[pos] != ")")
            {
                return null;
            }

            pos++;
            return inner;
        }

        if (pos + 1 < tokens.Count && tokens[pos + 1] == "(" && FilterFunctionNames.Contains(tokens[pos]))
        {
            var funcName = tokens[pos].ToLowerInvariant();
            pos += 2;
            if (pos + 3 >= tokens.Count || tokens[pos + 1] != "," || tokens[pos + 3] != ")")
            {
                return null;
            }

            var propName = tokens[pos];
            var literal = UnwrapFilterLiteral(tokens[pos + 2]);
            pos += 4;
            return new FunctionCallNode(funcName, propName, literal);
        }

        if (pos + 2 < tokens.Count && string.Equals(tokens[pos + 1], "in", StringComparison.OrdinalIgnoreCase) && tokens[pos + 2] == "(")
        {
            var propName = tokens[pos];
            pos += 3;

            var literals = new List<FilterLiteral>();
            while (pos < tokens.Count && tokens[pos] != ")")
            {
                literals.Add(ParseLiteral(tokens[pos]));
                pos++;
                if (pos < tokens.Count && tokens[pos] == ",")
                {
                    pos++;
                }
                else if (pos < tokens.Count && tokens[pos] != ")")
                {
                    // a comma has to separate every entry except the last
                    return null;
                }
            }

            if (literals.Count == 0 || pos >= tokens.Count || tokens[pos] != ")")
            {
                return null;
            }

            pos++;
            return new InNode(propName, literals);
        }

        if (pos + 2 < tokens.Count && FilterComparisonOps.Contains(tokens[pos + 1]))
        {
            var propName = tokens[pos];
            var op = tokens[pos + 1].ToLowerInvariant();
            var literal = ParseLiteral(tokens[pos + 2]);
            pos += 3;
            return new ComparisonNode(propName, op, literal);
        }

        return null;
    }

    private static FilterLiteral ParseLiteral(string token) => new(
        UnwrapFilterLiteral(token),
        IsNull: token.Length > 0 && token[0] != '\'' && string.Equals(token, "null", StringComparison.OrdinalIgnoreCase));

    private static string UnwrapFilterLiteral(string token) =>
        token.Length >= 2 && token[0] == '\'' && token[^1] == '\''
            ? token[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : token;

    // --- $search (directory objects only) ---

    private abstract record SearchNode
    {
        public abstract bool Evaluate(JsonObject item);
    }

    private sealed record SearchLogicalNode(SearchNode Left, SearchNode Right, bool IsAnd) : SearchNode
    {
        public override bool Evaluate(JsonObject item) =>
            IsAnd ? Left.Evaluate(item) && Right.Evaluate(item) : Left.Evaluate(item) || Right.Evaluate(item);
    }

    // Real Graph's tokenized matching only applies to displayName/description
    // - every other string property falls back to plain $filter startswith
    // semantics (both confirmed directly in the doc). "true search" (token-set
    // containment, order-independent, case-insensitive) is what makes
    // "McGowan Irene" match a displayName of "Irene McGowan".
    private sealed record SearchClauseNode(string PropName, string Text) : SearchNode
    {
        private static readonly string[] TokenizedProperties = ["displayName", "description"];

        public override bool Evaluate(JsonObject item)
        {
            var value = FindPropertyValue(item, PropName);
            if (value is null)
            {
                return false;
            }

            if (TokenizedProperties.Contains(PropName, StringComparer.OrdinalIgnoreCase))
            {
                var searchTokens = Tokenize(Text);
                if (searchTokens.Count == 0)
                {
                    return false;
                }

                var valueTokens = Tokenize(value.ToString());
                return searchTokens.All(valueTokens.Contains);
            }

            return value.ToString().StartsWith(Text, StringComparison.Ordinal);
        }
    }

    // A simplified approximation of Microsoft's real tokenizer (documented at
    // https://learn.microsoft.com/graph/search-query-parameter): splits on
    // non-alphanumeric runs, lower-to-upper case transitions (camelCase), and
    // letter/digit transitions, lowercasing everything for comparison. Doesn't
    // replicate the real tokenizer's extra quirk of also emitting symbols as
    // their own token and a no-separator concatenated variant (e.g.
    // "hello.world" additionally tokenizing to a bare "helloworld") - a mock
    // doesn't need that level of fidelity to be useful for testing that
    // tokenized (not substring) matching is happening at all.
    private static HashSet<string> Tokenize(string input)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        char? prev = null;

        void Flush(int end)
        {
            if (end > start)
            {
                tokens.Add(input[start..end].ToLowerInvariant());
            }
        }

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush(i);
                start = i + 1;
                prev = null;
                continue;
            }

            if (prev is { } p && ((char.IsLower(p) && char.IsUpper(c)) || (char.IsDigit(p) != char.IsDigit(c))))
            {
                Flush(i);
                start = i;
            }

            prev = c;
        }

        Flush(input.Length);
        return tokens;
    }

    private static readonly Regex SearchTokenRegex = new("\"(?:[^\"\\\\]|\\\\.)*\"|[()]|[^\\s()]+", RegexOptions.None, TimeSpan.FromSeconds(5));

    // General format per the doc: "clause1" [AND | OR] "clauseX", parentheses
    // for precedence, AND/OR required to be uppercase and outside the quotes.
    // Each clause is "property:text"; a bare quoted clause with no ":" isn't
    // covered by an explicit example in the doc for directory objects
    // specifically (unlike messages, which default to from/subject/body) -
    // defaulting it to displayName is a reasonable stand-in, not a confirmed
    // real behavior, since it's the one property every directory object type
    // actually has.
    private static SearchNode? TryParseSearch(string search)
    {
        var tokens = SearchTokenRegex.Matches(search).Select(m => m.Value).ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        var pos = 0;
        var node = ParseSearchOr(tokens, ref pos);
        return node is not null && pos == tokens.Count ? node : null;
    }

    private static SearchNode? ParseSearchOr(List<string> tokens, ref int pos)
    {
        var left = ParseSearchAnd(tokens, ref pos);
        while (left is not null && pos < tokens.Count && tokens[pos] == "OR")
        {
            pos++;
            var right = ParseSearchAnd(tokens, ref pos);
            left = right is null ? null : new SearchLogicalNode(left, right, IsAnd: false);
        }

        return left;
    }

    private static SearchNode? ParseSearchAnd(List<string> tokens, ref int pos)
    {
        var left = ParseSearchPrimary(tokens, ref pos);
        while (left is not null && pos < tokens.Count && tokens[pos] == "AND")
        {
            pos++;
            var right = ParseSearchPrimary(tokens, ref pos);
            left = right is null ? null : new SearchLogicalNode(left, right, IsAnd: true);
        }

        return left;
    }

    private static SearchNode? ParseSearchPrimary(List<string> tokens, ref int pos)
    {
        if (pos >= tokens.Count)
        {
            return null;
        }

        if (tokens[pos] == "(")
        {
            pos++;
            var inner = ParseSearchOr(tokens, ref pos);
            if (inner is null || pos >= tokens.Count || tokens[pos] != ")")
            {
                return null;
            }

            pos++;
            return inner;
        }

        var token = tokens[pos];
        if (token.Length < 2 || token[0] != '"' || token[^1] != '"')
        {
            // Anything else here - most commonly a lowercase "and"/"or",
            // which the doc requires to be uppercase - is malformed.
            return null;
        }

        var unescaped = UnescapeSearchClause(token[1..^1]);
        var colonIdx = unescaped.IndexOf(':', StringComparison.Ordinal);
        var propName = colonIdx < 0 ? "displayName" : unescaped[..colonIdx];
        var text = colonIdx < 0 ? unescaped : unescaped[(colonIdx + 1)..];

        pos++;
        return new SearchClauseNode(propName, text);
    }

    private static string UnescapeSearchClause(string raw)
    {
        var result = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
            }

            result.Append(raw[i]);
        }

        return result.ToString();
    }

    // Entity-set/singleton-level restrictions take precedence over the type-level
    // default when both exist, same as real OData annotation target resolution
    // (a more specific target wins) - see SchemaRegistry's field comments for why
    // that split exists at all.
    private static QueryRestrictions? GetRestrictions(SchemaRegistry registry, string? setOrSingletonName, string typeFullName)
    {
        if (setOrSingletonName is not null && registry.EntitySetRestrictions.TryGetValue(setOrSingletonName, out var setRestrictions))
        {
            return setRestrictions;
        }

        return registry.TypeRestrictions.TryGetValue(typeFullName, out var typeRestrictions) ? typeRestrictions : null;
    }

    // --- CSDL parsing ---

    private static SchemaRegistry ParseSchema(string csdl)
    {
        var registry = new SchemaRegistry();
        var schemaStartRegex = new Regex(@"<Schema Namespace=""([^""]+)""(?:\s+Alias=""([^""]+)"")?[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(30));
        var starts = schemaStartRegex.Matches(csdl);

        foreach (Match s in starts)
        {
            if (s.Groups[2].Success)
            {
                registry.AliasMap[s.Groups[2].Value] = s.Groups[1].Value;
            }
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var namespaceName = starts[i].Groups[1].Value;
            var blockStart = starts[i].Index + starts[i].Length;
            var blockEnd = i + 1 < starts.Count ? starts[i + 1].Index : csdl.Length;
            var body = csdl[blockStart..blockEnd];

            ParseTypeBlocks(registry, namespaceName, body, "EntityType");
            ParseTypeBlocks(registry, namespaceName, body, "ComplexType");
            ParseEnumBlocks(registry, namespaceName, body);
            ParseOperationBlocks(registry, body, "Function", isAction: false);
            ParseOperationBlocks(registry, body, "Action", isAction: true);
        }

        var containerMatch = Regex.Match(csdl, @"<EntityContainer Name=""[^""]*"">(.*?)</EntityContainer>", RegexOptions.Singleline, TimeSpan.FromSeconds(30));
        if (!containerMatch.Success)
        {
            return registry;
        }

        var containerBody = containerMatch.Groups[1].Value;
        foreach (Match m in Regex.Matches(containerBody, @"<EntitySet Name=""([^""]+)"" EntityType=""([^""]+)"""))
        {
            registry.EntitySets[m.Groups[1].Value] = m.Groups[2].Value;
        }

        foreach (Match m in Regex.Matches(containerBody, @"<Singleton Name=""([^""]+)"" Type=""([^""]+)"""))
        {
            registry.Singletons[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // Needs EntitySets/Singletons already populated, to tell an entity-set-level
        // annotation target apart from a type-level one - see ParseCapabilityRestrictions.
        ParseCapabilityRestrictions(registry, csdl);

        return registry;
    }

    // Restriction annotations (Org.OData.Capabilities.V1.{Filter,Sort,Expand}Restrictions)
    // sit in top-level <Annotations Target="..."> blocks, not nested inside the
    // EntityType/EntitySet they describe - Target is either an entity type's full
    // name (the dominant real-world pattern - confirmed: 73/73 FilterRestrictions,
    // 96/101 ExpandRestrictions target a type this way) or "{container}/{entitySet
    // or singleton name}" for a set-specific override (rarer, but where "users" own
    // chats/joinedTeams/etc. exclusions actually live). Rather than reconstructing
    // the exact container-qualified name to detect the latter, this just checks
    // whether Target's last path segment matches an already-known entity set or
    // singleton name - simpler, and just as reliable given Graph's naming never
    // collides between the two.
    private static void ParseCapabilityRestrictions(SchemaRegistry registry, string csdl)
    {
        foreach (Match ann in Regex.Matches(csdl, @"<Annotations Target=""([^""]+)"">(.*?)</Annotations>", RegexOptions.Singleline, TimeSpan.FromSeconds(30)))
        {
            var restrictions = ParseRestrictionsFromAnnotationsBody(ann.Groups[2].Value);
            if (restrictions is null)
            {
                continue;
            }

            var targetRaw = ann.Groups[1].Value;
            var slashIdx = targetRaw.LastIndexOf('/');
            var possibleSetName = slashIdx >= 0 ? targetRaw[(slashIdx + 1)..] : null;
            if (possibleSetName is not null &&
                (registry.EntitySets.ContainsKey(possibleSetName) || registry.Singletons.ContainsKey(possibleSetName)))
            {
                registry.EntitySetRestrictions[possibleSetName] = restrictions;
                continue;
            }

            var resolvedType = ResolveRefName(registry, targetRaw);
            if (registry.Types.ContainsKey(resolvedType))
            {
                registry.TypeRestrictions[resolvedType] = restrictions;
            }
        }
    }

    private static QueryRestrictions? ParseRestrictionsFromAnnotationsBody(string body)
    {
        QueryRestrictions? restrictions = null;

        var filterMatch = Regex.Match(body, @"<Annotation Term=""Org\.OData\.Capabilities\.V1\.FilterRestrictions""[^>]*>(.*?)</Annotation>", RegexOptions.Singleline, TimeSpan.FromSeconds(30));
        if (filterMatch.Success)
        {
            // Provably the first assignment (restrictions is declared null right
            // above, on every path) - a plain assignment here, not `??=`, since the
            // analyzer flags the null-check as dead code otherwise (CA1508).
            restrictions = new QueryRestrictions();
            restrictions.Filterable = ParseBoolProperty(filterMatch.Groups[1].Value, "Filterable", defaultValue: true);
            restrictions.NonFilterableProperties.UnionWith(ParsePropertyPathCollection(filterMatch.Groups[1].Value, "NonFilterableProperties", "PropertyPath"));
        }

        var sortMatch = Regex.Match(body, @"<Annotation Term=""Org\.OData\.Capabilities\.V1\.SortRestrictions""[^>]*>(.*?)</Annotation>", RegexOptions.Singleline, TimeSpan.FromSeconds(30));
        if (sortMatch.Success)
        {
            restrictions ??= new QueryRestrictions();
            restrictions.Sortable = ParseBoolProperty(sortMatch.Groups[1].Value, "Sortable", defaultValue: true);
            restrictions.NonSortableProperties.UnionWith(ParsePropertyPathCollection(sortMatch.Groups[1].Value, "NonSortableProperties", "PropertyPath"));
        }

        var expandMatch = Regex.Match(body, @"<Annotation Term=""Org\.OData\.Capabilities\.V1\.ExpandRestrictions""[^>]*>(.*?)</Annotation>", RegexOptions.Singleline, TimeSpan.FromSeconds(30));
        if (expandMatch.Success)
        {
            restrictions ??= new QueryRestrictions();
            restrictions.Expandable = ParseBoolProperty(expandMatch.Groups[1].Value, "Expandable", defaultValue: true);
            restrictions.NonExpandableProperties.UnionWith(ParsePropertyPathCollection(expandMatch.Groups[1].Value, "NonExpandableProperties", "NavigationPropertyPath"));
        }

        return restrictions;
    }

    private static bool ParseBoolProperty(string recordBody, string propertyName, bool defaultValue)
    {
        var m = Regex.Match(recordBody, $@"<PropertyValue Property=""{propertyName}""\s+Bool=""([^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(5));
        return m.Success ? string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) : defaultValue;
    }

    private static HashSet<string> ParsePropertyPathCollection(string recordBody, string propertyName, string pathElementTag)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collectionMatch = Regex.Match(recordBody, $@"<PropertyValue Property=""{propertyName}"">\s*<Collection>(.*?)</Collection>", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        if (!collectionMatch.Success)
        {
            return result;
        }

        foreach (Match p in Regex.Matches(collectionMatch.Groups[1].Value, $@"<{pathElementTag}>([^<]+)</{pathElementTag}>"))
        {
            result.Add(p.Groups[1].Value);
        }

        return result;
    }

    private static void ParseTypeBlocks(SchemaRegistry registry, string namespaceName, string body, string tagName)
    {
        var pattern = $@"<{tagName} Name=""([^""]+)""(?:\s+BaseType=""([^""]+)"")?[^>]*?(?:/>|>(.*?)</{tagName}>)";
        foreach (Match m in Regex.Matches(body, pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(30)))
        {
            var name = m.Groups[1].Value;
            var baseType = m.Groups[2].Success ? m.Groups[2].Value : "";
            var inner = m.Groups[3].Success ? m.Groups[3].Value : "";

            var def = new TypeDef { BaseType = baseType };
            foreach (Match p in Regex.Matches(inner, @"<Property Name=""([^""]+)"" Type=""([^""]+)""[^>]*?(?:/>|>.*?</Property>)", RegexOptions.Singleline))
            {
                def.Properties.Add((p.Groups[1].Value, p.Groups[2].Value));
            }

            foreach (Match np in Regex.Matches(inner, @"<NavigationProperty Name=""([^""]+)"" Type=""([^""]+)""[^>]*?(?:/>|>.*?</NavigationProperty>)", RegexOptions.Singleline))
            {
                def.NavigationProperties.Add((np.Groups[1].Value, np.Groups[2].Value));
            }

            registry.Types[$"{namespaceName}.{name}"] = def;
        }
    }

    private static void ParseEnumBlocks(SchemaRegistry registry, string namespaceName, string body)
    {
        foreach (Match m in Regex.Matches(body, @"<EnumType Name=""([^""]+)""[^>]*>(.*?)</EnumType>", RegexOptions.Singleline, TimeSpan.FromSeconds(30)))
        {
            var name = m.Groups[1].Value;
            var inner = m.Groups[2].Value;
            var members = Regex.Matches(inner, @"<Member Name=""([^""]+)""")
                .Select(mm => mm.Groups[1].Value)
                .ToList();
            registry.EnumTypes[$"{namespaceName}.{name}"] = members;
        }
    }

    // Bound Function/Action elements sit at schema level, as siblings of EntityType -
    // not nested inside it - and their binding parameter's name varies across the real
    // file ("bindingParameter", "bindingparameter", "bindparameter"), so it's identified
    // by being the first <Parameter>, never by name.
    private static void ParseOperationBlocks(SchemaRegistry registry, string body, string tagName, bool isAction)
    {
        var pattern = $@"<{tagName} Name=""([^""]+)""[^>]*?IsBound=""true""[^>]*?(?:/>|>(.*?)</{tagName}>)";
        foreach (Match m in Regex.Matches(body, pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(30)))
        {
            var name = m.Groups[1].Value;
            var inner = m.Groups[2].Success ? m.Groups[2].Value : "";

            var bindingMatch = Regex.Match(inner, @"<Parameter Name=""[^""]+""\s+Type=""([^""]+)""");
            if (!bindingMatch.Success)
            {
                continue;
            }

            var bindingType = bindingMatch.Groups[1].Value;
            var isCollectionBound = IsCollectionType(bindingType);
            var boundTo = ResolveRefName(registry, StripCollection(bindingType));

            var returnMatch = Regex.Match(inner, @"<ReturnType Type=""([^""]+)""");
            var def = new FunctionActionDef
            {
                Name = name,
                IsAction = isAction,
                IsCollectionBound = isCollectionBound,
                ReturnType = returnMatch.Success ? returnMatch.Groups[1].Value : null,
            };

            var table = isAction ? registry.ActionsByBindingType : registry.FunctionsByBindingType;
            if (!table.TryGetValue(boundTo, out var list))
            {
                table[boundTo] = list = [];
            }

            list.Add(def);
        }
    }

    private static List<FunctionActionDef> GetAllBoundOperations(SchemaRegistry registry, string fullName, bool isCollection, Dictionary<string, List<FunctionActionDef>> table)
    {
        var result = new List<FunctionActionDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = fullName;
        while (current is not null && seen.Add(current))
        {
            if (table.TryGetValue(current, out var ops))
            {
                result.AddRange(ops.Where(o => o.IsCollectionBound == isCollection));
            }

            current = registry.Types.TryGetValue(current, out var def) && !string.IsNullOrEmpty(def.BaseType)
                ? ResolveRefName(registry, def.BaseType)
                : null;
        }

        return result;
    }

    // A parameterless bound Function is commonly invoked without empty parens in
    // real Graph usage (e.g. GET /users/delta rather than /users/delta()), so
    // parens presence can't reliably distinguish a Function call from an Action
    // call - look the bare name up in both tables instead. Functions are checked
    // first since a name collision between a Function and an Action bound to the
    // same type would be unusual; HandleFunction/HandleAction each still reject
    // the wrong HTTP method regardless of which table produced the match.
    private static FunctionActionDef? TryMatchBoundOperation(SchemaRegistry registry, ResolvedNode current, string segment)
    {
        var parenIdx = segment.IndexOf('(', StringComparison.Ordinal);
        var bareName = parenIdx >= 0 ? segment[..parenIdx] : segment;

        return GetAllBoundOperations(registry, current.TypeFullName, current.IsCollection, registry.FunctionsByBindingType)
                .FirstOrDefault(o => string.Equals(o.Name, bareName, StringComparison.OrdinalIgnoreCase))
            ?? GetAllBoundOperations(registry, current.TypeFullName, current.IsCollection, registry.ActionsByBindingType)
                .FirstOrDefault(o => string.Equals(o.Name, bareName, StringComparison.OrdinalIgnoreCase));
    }

    // --- inherited-member resolution ---

    private static List<(string Name, string Type)> GetAllProperties(SchemaRegistry registry, string fullName)
    {
        var result = new List<(string Name, string Type)>();
        CollectMembers(registry, fullName, result, [], static def => def.Properties);
        return result;
    }

    private static List<(string Name, string Type)> GetAllNavigationProperties(SchemaRegistry registry, string fullName)
    {
        var result = new List<(string Name, string Type)>();
        CollectMembers(registry, fullName, result, [], static def => def.NavigationProperties);
        return result;
    }

    private static void CollectMembers(
        SchemaRegistry registry,
        string fullName,
        List<(string Name, string Type)> result,
        HashSet<string> seen,
        Func<TypeDef, List<(string Name, string Type)>> selector)
    {
        if (!seen.Add(fullName))
        {
            return;
        }

        if (!registry.Types.TryGetValue(fullName, out var def))
        {
            return;
        }

        if (!string.IsNullOrEmpty(def.BaseType))
        {
            CollectMembers(registry, ResolveRefName(registry, def.BaseType), result, seen, selector);
        }

        result.AddRange(selector(def));
    }
}

// A minimal request shape both a real intercepted HTTP request and a
// synthetic $batch sub-request (see HandleBatch) can satisfy. RequestUri
// stays a plain System.Uri so every .RequestUri.Query/.AbsolutePath call
// site works unchanged; header lookup and the body are abstracted so any
// host (Dev Proxy, an in-process HttpMessageHandler, ...) can supply them.
public sealed class MockRequest(Uri requestUri, Func<string, string?> getHeader, string? body)
{
    public Uri RequestUri { get; } = requestUri;
    public string? Body { get; } = body;
    public string? GetHeader(string name) => getHeader(name);
}

public readonly record struct SchemaStats(bool Loaded, int EntityTypes, int Enums, int EntitySets, int Singletons);

/// <summary>Why the engine left a request alone; the host decides what happens next (forward, 404, ...).</summary>
public enum MockSkipReason
{
    UnsupportedMethod,
    UnknownVersion,
    NoSchemaMatch,
    MethodNotSupportedForResource,
}

/// <summary>A finished mock response. <see cref="LogLabel"/> is a short description for host logging.</summary>
public sealed record MockResponse(int StatusCode, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body, string? LogLabel);

public readonly record struct MockOutcome(MockResponse? Response, MockSkipReason? SkipReason)
{
    public bool Handled => Response is not null;

    public static MockOutcome Respond(MockResponse response) => new(response, null);

    public static MockOutcome NotHandled(MockSkipReason reason) => new(null, reason);
}

/// <summary>
/// One type's records: insertion-ordered like the List it replaces, plus an ordinal id index so
/// by-id lookups are O(1). Duplicate ids keep list semantics (the first one in order wins).
/// Not thread-safe on its own; GraphMockEngine serializes all access.
/// </summary>
internal sealed class EntityPool : IEnumerable<JsonObject>
{
    private readonly List<JsonObject> _items;
    private readonly Dictionary<string, JsonObject> _byId = new(StringComparer.Ordinal);

    public EntityPool(IEnumerable<JsonObject> items)
    {
        _items = [.. items];
        Reindex();
    }

    public int Count => _items.Count;

    public JsonObject this[int index] => _items[index];

    public void Add(JsonObject item)
    {
        _items.Add(item);
        if (GetId(item) is { } id)
        {
            _ = _byId.TryAdd(id, item);
        }
    }

    public bool Remove(JsonObject item)
    {
        if (!_items.Remove(item))
        {
            return false;
        }

        if (GetId(item) is { } id && _byId.TryGetValue(id, out var indexed) && ReferenceEquals(indexed, item))
        {
            // Another record may share the id; the next one in order now wins.
            Reindex();
        }

        return true;
    }

    public JsonObject? FindById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Rebuilds the index, e.g. after a PATCH/PUT body changed a record's id.</summary>
    public void Reindex()
    {
        _byId.Clear();
        foreach (var item in _items)
        {
            if (GetId(item) is { } id)
            {
                _ = _byId.TryAdd(id, item);
            }
        }
    }

    public IEnumerator<JsonObject> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private static string? GetId(JsonObject item) =>
        item.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idValue && idValue.TryGetValue(out string? idStr)
            ? idStr
            : null;
}

/// <summary>How later pages relate to the first one.</summary>
public enum GraphMockPagingConsistency
{
    /// <summary>The result set is frozen when page 1 is served, so a full scan sees exactly that membership.</summary>
    Snapshot,

    /// <summary>Each page re-evaluates the query and skips by offset, so concurrent inserts and deletes shift items.</summary>
    Offset,
}

public sealed class GraphMockPageSizeConfiguration
{
    public int? Default { get; set; }
    public int? Max { get; set; }
}

/// <summary>Paging settings. Defaults follow Graph's directory APIs: 100 per page, 999 at most.</summary>
public sealed class GraphMockPagingOptions
{
    public int DefaultPageSize { get; set; } = 100;
    public int MaxPageSize { get; set; } = 999;
    public GraphMockPagingConsistency Consistency { get; set; } = GraphMockPagingConsistency.Snapshot;

    /// <summary>Per-type overrides keyed by full type name, e.g. "microsoft.graph.message".</summary>
    public Dictionary<string, GraphMockPageSizeConfiguration> PageSizes { get; } = new(StringComparer.Ordinal);

    internal (int Default, int Max) SizesFor(string typeFullName)
    {
        var max = Math.Max(1, MaxPageSize);
        var def = DefaultPageSize;
        if (PageSizes.TryGetValue(typeFullName, out var overrides))
        {
            max = Math.Max(1, overrides.Max ?? max);
            def = overrides.Default ?? def;
        }

        return (Math.Clamp(def, 1, max), max);
    }
}
