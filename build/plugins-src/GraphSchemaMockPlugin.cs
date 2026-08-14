// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

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

public sealed class GraphSchemaMockPluginConfiguration
{
    public string SchemaFilePath { get; set; } = "graph-schema/v1.0.csdl";
    // Optional: if unset, or the file doesn't exist, /beta/* requests are simply
    // left unmocked (falling through to whatever the next plugin/upstream does),
    // same as this plugin's behavior before beta support existed.
    public string? BetaSchemaFilePath { get; set; } = "graph-schema/beta.csdl";
    // Seeds the subscribedSkus pool from these instead of generic fabrication,
    // when non-empty - see GraphSchemaMockSubscribedSkuConfiguration.
    public IEnumerable<GraphSchemaMockSubscribedSkuConfiguration> SubscribedSkus { get; set; } = [];
}

/// <summary>
/// Generates mock responses for any Microsoft Graph v1.0 or beta endpoint by
/// resolving the request path against the real CSDL schema for that version
/// (EntitySets, Singletons and NavigationProperties) and fabricating a
/// schema-accurate object on the fly, instead of requiring a pre-authored
/// fixture per endpoint.
/// </summary>
public sealed class GraphSchemaMockPlugin(
    HttpClient httpClient,
    ILogger<GraphSchemaMockPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<GraphSchemaMockPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
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
    /// </summary>
    private readonly record struct ResolvedNode(
        string TypeFullName,
        bool IsCollection,
        string? ItemId,
        bool IsRef = false,
        bool IsValue = false,
        bool IsCount = false,
        FunctionActionDef? Operation = null,
        string? PropName = null);

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

    private static readonly Dictionary<string, Func<string>> NameFakes = new(StringComparer.Ordinal)
    {
        ["id"] = () => Guid.NewGuid().ToString(),
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

    private SchemaRegistry _v1Registry = new();
    private SchemaRegistry? _betaRegistry;

    // One shared pool of generated records per resolved type, seeded lazily on
    // first access and mutated by POST/PATCH/PUT/DELETE, so repeated requests
    // within a session see consistent, workable data instead of fresh random
    // values every call. Shared across v1.0 and beta: they model the same
    // underlying tenant data, just through differently-shaped API surfaces, so a
    // record seeded via one version's schema is reused (and simply missing
    // whichever properties are beta-only/v1.0-only) rather than kept separate.
    private readonly Dictionary<string, List<JsonObject>> _store = new(StringComparer.Ordinal);

    public override string Name => nameof(GraphSchemaMockPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        var schemaFilePath = ProxyUtils.GetFullPath(Configuration.SchemaFilePath, ProxyConfiguration.ConfigFile);
        if (!File.Exists(schemaFilePath))
        {
            Enabled = false;
            throw new FileNotFoundException($"Graph schema file '{schemaFilePath}' does not exist.", schemaFilePath);
        }

        _v1Registry = ParseSchema(await File.ReadAllTextAsync(schemaFilePath, cancellationToken));
        LogRegistryLoaded(_v1Registry, "v1.0", schemaFilePath);

        if (!string.IsNullOrWhiteSpace(Configuration.BetaSchemaFilePath))
        {
            var betaSchemaFilePath = ProxyUtils.GetFullPath(Configuration.BetaSchemaFilePath, ProxyConfiguration.ConfigFile);
            if (File.Exists(betaSchemaFilePath))
            {
                _betaRegistry = ParseSchema(await File.ReadAllTextAsync(betaSchemaFilePath, cancellationToken));
                LogRegistryLoaded(_betaRegistry, "beta", betaSchemaFilePath);
            }
            else
            {
                Logger.LogWarning("Beta Graph schema file '{BetaSchemaFilePath}' does not exist - /beta/* requests will not be mocked.", betaSchemaFilePath);
            }
        }
    }

    private void LogRegistryLoaded(SchemaRegistry registry, string version, string schemaFilePath)
    {
        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "{Name} loaded {EntityCount} entity types, {EnumCount} enums, {EntitySetCount} entity sets and {SingletonCount} singletons for {Version} from {SchemaFilePath}",
                Name,
                registry.Types.Count,
                registry.EnumTypes.Count,
                registry.EntitySets.Count,
                registry.Singletons.Count,
                version,
                schemaFilePath);
        }
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.ShouldExecute(UrlsToWatch))
        {
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;
        var method = request.Method?.ToUpperInvariant() ?? "";
        if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE") ||
            !ProxyUtils.IsGraphUrl(request.RequestUri))
        {
            return Task.CompletedTask;
        }

        var (registry, versionSegment) = ResolveRegistry(request.RequestUri.AbsolutePath);
        if (registry is null)
        {
            return Task.CompletedTask;
        }

        var path = request.RequestUri.AbsolutePath;
        var versionIndex = path.IndexOf(versionSegment, StringComparison.OrdinalIgnoreCase);
        var remainder = path[(versionIndex + versionSegment.Length)..].Trim('/');
        if (remainder.Length == 0)
        {
            return Task.CompletedTask;
        }

        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resolved = ResolvePath(registry, segments);
        if (resolved is null)
        {
            Logger.LogRequest("No schema match for this path", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var node = resolved.Value;
        if (node.IsValue || node.IsCount)
        {
            HandleRawSegment(registry, node, method, request, e);
            return Task.CompletedTask;
        }

        var (responseBody, statusCode) = node.IsRef
            ? HandleRef(node, method, request)
            : node.Operation is not null
                ? (node.Operation.IsAction ? HandleAction(registry, node.Operation, method, request) : HandleFunction(registry, node.Operation, method, request))
                : node.IsCollection
                    ? HandleCollection(registry, versionSegment, node.TypeFullName, method, request)
                    : node.ItemId is not null
                        ? HandleItem(registry, versionSegment, node.TypeFullName, node.ItemId, method, request)
                        : HandleSingleton(registry, versionSegment, node.TypeFullName, method, request);

        if (statusCode is null)
        {
            // method not supported for this resource shape - let it fall through
            return Task.CompletedTask;
        }

        var requestId = Guid.NewGuid().ToString();
        var requestDate = DateTime.Now.ToString("r", CultureInfo.InvariantCulture);
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);

        e.Session.GenericResponse(responseBody?.ToJsonString() ?? string.Empty, statusCode.Value, headers.Select(h => new HttpHeader(h.Name, h.Value)));
        e.ResponseState.HasBeenSet = true;

        Logger.LogRequest($"{(int)statusCode.Value} schema mock ({node.TypeFullName})", MessageType.Mocked, new LoggingContext(e.Session));

        return Task.CompletedTask;
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

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleCollection(SchemaRegistry registry, string versionSegment, string typeFullName, string method, Request request)
    {
        var pool = GetOrSeedPool(registry, typeFullName);

        if (method == "GET")
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var filterParam = query["$filter"];

            IEnumerable<JsonObject> filtered = pool;
            if (!string.IsNullOrWhiteSpace(filterParam))
            {
                var filterNode = TryParseFilter(filterParam);
                if (filterNode is null)
                {
                    return (ODataError("BadRequest", $"Unable to parse $filter '{filterParam}'."), HttpStatusCode.BadRequest);
                }

                filtered = filtered.Where(filterNode.Evaluate);
            }

            var selectedProps = GetSelectedProps(registry, typeFullName, request);
            var array = new JsonArray();
            foreach (var item in filtered)
            {
                var trimmed = TrimTo(item, selectedProps);
                ApplyExpand(registry, trimmed, typeFullName, request);
                array.Add(trimmed);
            }

            return (new JsonObject { ["@odata.context"] = BuildODataContext(versionSegment, request), ["value"] = array }, HttpStatusCode.OK);
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
                created["id"] = Guid.NewGuid().ToString();
            }

            pool.Add(created);
            var createdBody = created.DeepClone().AsObject();
            createdBody["@odata.context"] = BuildODataContext(versionSegment, request) + "/$entity";
            return (createdBody, HttpStatusCode.Created);
        }

        return (null, null);
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
    private static string BuildODataContext(string versionSegment, Request request)
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

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleItem(SchemaRegistry registry, string versionSegment, string typeFullName, string itemId, string method, Request request)
    {
        var pool = GetOrSeedPool(registry, typeFullName);
        var existing = FindById(pool, itemId);

        if (existing is null)
        {
            return (ODataError("Request_ResourceNotFound", $"Resource '{itemId}' does not exist or one of its queried reference-property objects are not present."), HttpStatusCode.NotFound);
        }

        switch (method)
        {
            case "GET":
                {
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
                    }

                    return (existing.DeepClone(), HttpStatusCode.OK);
                }
            default:
                return (null, null);
        }
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleSingleton(SchemaRegistry registry, string versionSegment, string typeFullName, string method, Request request)
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
    private static (JsonNode? Body, HttpStatusCode? StatusCode) HandleRef(ResolvedNode node, string method, Request request)
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

    private static (JsonNode? Body, HttpStatusCode? StatusCode) HandleAddRef(Request request)
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
    private static (JsonNode? Body, HttpStatusCode? StatusCode) HandleFunction(SchemaRegistry registry, FunctionActionDef op, string method, Request request)
    {
        if (method != "GET")
        {
            return (null, null);
        }

        return BuildOperationResult(registry, op.ReturnType);
    }

    private static (JsonNode? Body, HttpStatusCode? StatusCode) HandleAction(SchemaRegistry registry, FunctionActionDef op, string method, Request request)
    {
        if (method != "POST")
        {
            return (null, null);
        }

        return BuildOperationResult(registry, op.ReturnType);
    }

    private static (JsonNode? Body, HttpStatusCode? StatusCode) BuildOperationResult(SchemaRegistry registry, string? returnType)
    {
        if (returnType is null)
        {
            return (null, HttpStatusCode.NoContent);
        }

        var value = BuildValue(registry, returnType, "value", 1);
        return IsCollectionType(returnType)
            ? (new JsonObject { ["value"] = value }, HttpStatusCode.OK)
            : (value as JsonObject ?? new JsonObject { ["value"] = value }, HttpStatusCode.OK);
    }

    private void HandleRawSegment(SchemaRegistry registry, ResolvedNode node, string method, Request request, ProxyRequestArgs e)
    {
        if (method != "GET")
        {
            // falls through unmocked, consistent with the (null, null) convention elsewhere
            return;
        }

        var rawBody = node.IsCount
            ? GetOrSeedPool(registry, node.TypeFullName).Count.ToString(CultureInfo.InvariantCulture)
            : FakePrimitive(node.TypeFullName, node.PropName ?? "value")?.ToString() ?? "";

        e.Session.GenericResponse(rawBody, HttpStatusCode.OK, [new HttpHeader("Content-Type", "text/plain")]);
        e.ResponseState.HasBeenSet = true;

        Logger.LogRequest($"200 schema mock ({(node.IsCount ? "$count" : "$value")})", MessageType.Mocked, new LoggingContext(e.Session));
    }

    private static List<string> GetSelectedProps(SchemaRegistry registry, string typeFullName, Request request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var selectParam = query["$select"];
        return selectParam is not null ? SplitSelect(selectParam) : DefaultProps(registry, typeFullName);
    }

    private List<JsonObject> GetOrSeedPool(SchemaRegistry registry, string typeFullName)
    {
        if (_store.TryGetValue(typeFullName, out var pool))
        {
            return pool;
        }

        var seeded = string.Equals(typeFullName, "microsoft.graph.subscribedSku", StringComparison.Ordinal) && Configuration.SubscribedSkus.Any()
            ? SeedSubscribedSkuPool(registry, Configuration.SubscribedSkus)
            : SeedGenericPool(registry, typeFullName);

        _store[typeFullName] = seeded;
        return seeded;
    }

    private static List<JsonObject> SeedGenericPool(SchemaRegistry registry, string typeFullName)
    {
        var first = BuildEntity(registry, typeFullName);
        return [first, SecondSample(first, typeFullName)];
    }

    // Real Graph shares one accountId/accountName across every subscribedSku
    // in a tenant, and derives each sku's own "id" as "{accountId}_{skuId}"
    // rather than a bare guid - both confirmed against a real tenant's
    // subscribedSkus response.
    private static List<JsonObject> SeedSubscribedSkuPool(SchemaRegistry registry, IEnumerable<GraphSchemaMockSubscribedSkuConfiguration> skus)
    {
        var accountId = Guid.NewGuid().ToString();
        const string accountName = "contoso";
        return [.. skus.Select(sku => BuildSubscribedSku(registry, sku, accountId, accountName))];
    }

    private static JsonObject BuildSubscribedSku(SchemaRegistry registry, GraphSchemaMockSubscribedSkuConfiguration sku, string accountId, string accountName)
    {
        var entity = BuildEntity(registry, "microsoft.graph.subscribedSku");
        var skuId = Guid.NewGuid().ToString();
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
        entity["subscriptionIds"] = new JsonArray(Guid.NewGuid().ToString());

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

    private static JsonObject? FindById(List<JsonObject> pool, string id)
    {
        foreach (var item in pool)
        {
            if (item.TryGetPropertyValue("id", out var idNode) &&
                idNode is JsonValue idValue &&
                idValue.TryGetValue(out string? idStr) &&
                string.Equals(idStr, id, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static JsonObject? ParseBody(Request request)
    {
        var bodyString = request.BodyString;
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
            current = new ResolvedNode(ResolveRefName(registry, entitySetType), true, null);
        }
        else if (registry.Singletons.TryGetValue(segments[0], out var singletonType))
        {
            current = new ResolvedNode(ResolveRefName(registry, singletonType), false, null);
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

                // a segment right after a collection is always an item id
                current = new ResolvedNode(current.TypeFullName, false, segment);
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
            "Edm.Guid" => Guid.NewGuid().ToString(),
            "Edm.DateTimeOffset" => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            "Edm.Date" => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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
            clone["id"] = Guid.NewGuid().ToString();
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

        var scalarNames = GetAllProperties(registry, fullName)
            .Where(p => IsPrimitiveOrCollectionOfPrimitive(p.Type))
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, "id", StringComparison.Ordinal))
            .ToList();

        var result = new List<string> { "id" };
        result.AddRange(scalarNames.Take(10));
        return result;
    }

    private static bool IsPrimitiveOrCollectionOfPrimitive(string type)
    {
        var inner = type.StartsWith("Collection(", StringComparison.Ordinal) && type.EndsWith(')')
            ? type[11..^1]
            : type;
        return inner.StartsWith("Edm.", StringComparison.Ordinal);
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

    // Reuses BuildValue - the same recursive fabrication already used for nested
    // complex-type properties - rather than a parallel path, so expanded nav
    // properties get the same MaxDepth-capped, schema-accurate shape. Must run
    // after TrimTo, mutating the trimmed object: otherwise an expanded property
    // absent from $select/DefaultProps would immediately be stripped back out,
    // whereas real Graph always surfaces an expanded property regardless of $select.
    private static void ApplyExpand(SchemaRegistry registry, JsonObject target, string typeFullName, Request request)
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
                target[match.Value.Name] = BuildValue(registry, match.Value.Type, match.Value.Name, 1);
            }
        }
    }

    // --- $filter ---

    // Evaluated generically against whatever properties the plugin already
    // fabricates, not Graph's real per-property $filter restrictions - this is a
    // mock, not the real API, so replicating those restrictions isn't the point.
    private abstract record FilterNode
    {
        public abstract bool Evaluate(JsonObject item);
    }

    private sealed record NotNode(FilterNode Inner) : FilterNode
    {
        public override bool Evaluate(JsonObject item) => !Inner.Evaluate(item);
    }

    private sealed record LogicalNode(FilterNode Left, FilterNode Right, bool IsAnd) : FilterNode
    {
        public override bool Evaluate(JsonObject item) =>
            IsAnd ? Left.Evaluate(item) && Right.Evaluate(item) : Left.Evaluate(item) || Right.Evaluate(item);
    }

    private sealed record ComparisonNode(string PropName, string Op, string Literal) : FilterNode
    {
        public override bool Evaluate(JsonObject item)
        {
            var value = FindPropertyValue(item, PropName);
            if (value is null)
            {
                return false;
            }

            var cmp = CompareValues(value, Literal);
            if (cmp is null)
            {
                return false;
            }

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

        private static int? CompareValues(JsonValue value, string literal)
        {
            var valueStr = value.ToString();

            if (DateTimeOffset.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var literalDate) &&
                DateTimeOffset.TryParse(valueStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueDate))
            {
                return valueDate.CompareTo(literalDate);
            }

            if (double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var literalNum) &&
                double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var valueNum))
            {
                return valueNum.CompareTo(literalNum);
            }

            return string.CompareOrdinal(valueStr, literal);
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
    }

    private static JsonValue? FindPropertyValue(JsonObject item, string propName)
    {
        foreach (var kvp in item)
        {
            if (string.Equals(kvp.Key, propName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value as JsonValue;
            }
        }

        // Missing property: not an error, just never matches - consistent with
        // TrimTo already silently skipping properties it can't find.
        return null;
    }

    private static readonly HashSet<string> FilterComparisonOps = new(StringComparer.OrdinalIgnoreCase) { "eq", "ne", "gt", "lt", "ge", "le" };
    private static readonly HashSet<string> FilterFunctionNames = new(StringComparer.OrdinalIgnoreCase) { "startswith", "contains", "endswith" };
    private static readonly Regex FilterTokenRegex = new(@"'(?:[^']|'')*'|[()]|,|[^\s()',]+", RegexOptions.None, TimeSpan.FromSeconds(5));

    // Supports eq/ne/gt/lt/ge/le, and/or/not, and startswith/contains/endswith on
    // top-level scalar properties only - no lambda operators (any/all), no nested
    // property paths, no date arithmetic. Returns null on any parse failure, which
    // the caller turns into a 400 rather than silently ignoring the filter.
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

        if (pos + 2 < tokens.Count && FilterComparisonOps.Contains(tokens[pos + 1]))
        {
            var propName = tokens[pos];
            var op = tokens[pos + 1].ToLowerInvariant();
            var literal = UnwrapFilterLiteral(tokens[pos + 2]);
            pos += 3;
            return new ComparisonNode(propName, op, literal);
        }

        return null;
    }

    private static string UnwrapFilterLiteral(string token) =>
        token.Length >= 2 && token[0] == '\'' && token[^1] == '\''
            ? token[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : token;

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

        return registry;
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
