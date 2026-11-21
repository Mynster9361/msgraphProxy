// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;

namespace MsGraphProxy.MockEngine;

/// <summary>
/// msgraphProxy P1 #9-#11: data scoped per parent instance, a seeded relationship graph with real $ref,
/// and derived types / cast segments.
/// <list type="bullet">
/// <item>Contained navigations get their own pool per parent ("microsoft.graph.user/{id}/messages"); root sets
/// keep the type as their pool key, so their ids are unchanged.</item>
/// <item>Non-contained navigations (memberOf, members, manager, ...) are relationships: deterministic references to
/// records in the target type's home pools, changed by $ref POST/PUT/DELETE.</item>
/// <item>A cast segment (/members/microsoft.graph.user or graph.user) filters to a derived type, and items reached
/// through a relationship carry @odata.type.</item>
/// </list>
/// </summary>
public sealed partial class GraphMockEngine
{
    /// <summary>
    /// 404 for an unknown id anywhere in the path, like Graph. Off by default: older callers rely on
    /// /groups/{any-id}/members/$ref succeeding.
    /// </summary>
    public bool ValidatePathKeysEnabled { get; init; }

    private const int RelationshipSize = 3;

    // Relationship key "<owner scope>|<navigation>" -> referenced (type, id) pairs.
    private readonly Dictionary<string, List<(string Type, string Id)>> _relations = new(StringComparer.Ordinal);

    // Which type a directoryObject relationship points at, by navigation name. Without this a manager
    // could come back as an administrative unit.
    private static readonly Dictionary<string, string> RelationshipTargetByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["memberOf"] = "microsoft.graph.group",
        ["transitiveMemberOf"] = "microsoft.graph.group",
        ["members"] = "microsoft.graph.user",
        ["transitiveMembers"] = "microsoft.graph.user",
        ["owners"] = "microsoft.graph.user",
        ["registeredOwners"] = "microsoft.graph.user",
        ["registeredUsers"] = "microsoft.graph.user",
        ["manager"] = "microsoft.graph.user",
        ["directReports"] = "microsoft.graph.user",
        ["sponsors"] = "microsoft.graph.user",
        ["ownedObjects"] = "microsoft.graph.group",
        ["ownedDevices"] = "microsoft.graph.device",
        ["registeredDevices"] = "microsoft.graph.device",
        ["createdObjects"] = "microsoft.graph.application",
    };

    private static string Scope(string key, string id) => key + "/" + id;

    private static string? IdOf(JsonObject entity) =>
        entity["id"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;

    private static string TypeOf(JsonObject entity, string fallback) =>
        entity["@odata.type"] is JsonValue v && v.TryGetValue(out string? t) && t.Length > 1 ? t.TrimStart('#') : fallback;

    private static bool IsSameOrDerived(SchemaRegistry registry, string type, string baseType) =>
        string.Equals(type, baseType, StringComparison.Ordinal) || IsDerivedFrom(registry, type, baseType);

    private static bool IsContainedNav(SchemaRegistry registry, string typeFullName, string navName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = typeFullName;
        while (seen.Add(current) && registry.Types.TryGetValue(current, out var def))
        {
            if (def.ContainedNavigations.Contains(navName))
            {
                return true;
            }

            if (string.IsNullOrEmpty(def.BaseType))
            {
                break;
            }

            current = ResolveRefName(registry, def.BaseType);
        }

        return false;
    }

    /// <summary>"microsoft.graph.user" or "graph.user" when it names the current type or one derived from it.</summary>
    private static string? TryCast(SchemaRegistry registry, string currentType, string segment)
    {
        if (!segment.Contains('.', StringComparison.Ordinal) || segment.Contains('(', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var candidate in new[] { ResolveRefName(registry, segment), "microsoft." + segment })
        {
            if (registry.Types.ContainsKey(candidate) && IsSameOrDerived(registry, candidate, currentType))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The records a node's collection (or single navigation) holds right now, after any cast.</summary>
    private List<JsonObject> Members(SchemaRegistry registry, ResolvedNode node)
    {
        var seedType = node.SeedType ?? node.TypeFullName;
        IEnumerable<JsonObject> items = node.RelationKey is { } relation
            ? RelationTargets(registry, relation, seedType, node.IsCollection || node.ItemId is not null)
            : GetOrSeedPool(registry, seedType, node.PoolKey);
        if (node.CastType is { } cast)
        {
            items = items.Where(i => IsSameOrDerived(registry, TypeOf(i, seedType), cast));
        }

        return [.. items];
    }

    private JsonObject? FindMember(SchemaRegistry registry, ResolvedNode collection, string id)
    {
        if (collection.RelationKey is null && collection.CastType is null)
        {
            return GetOrSeedPool(registry, collection.SeedType ?? collection.TypeFullName, collection.PoolKey).FindById(id);
        }

        return Members(registry, collection).FirstOrDefault(m => IdOf(m) == id);
    }

    /// <summary>A clone (with @odata.type) of the record with this id in a home pool of a type derived from <paramref name="baseType"/>.</summary>
    private JsonObject? FindDerivedRecord(SchemaRegistry registry, string baseType, string id)
    {
        foreach (var type in registry.EntitySets.Values.Select(t => ResolveRefName(registry, t)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (type != baseType && IsDerivedFrom(registry, type, baseType) && GetOrSeedPool(registry, type).FindById(id) is { } record)
            {
                var clone = record.DeepClone().AsObject();
                clone["@odata.type"] = "#" + type;
                return clone;
            }
        }

        return null;
    }

    /// <summary>The scope children of this record live under: "{pool key}/{id}", or the node's own key for singletons.</summary>
    private static string OwnerOf(ResolvedNode node, JsonObject record)
    {
        if (node.ItemId is null && !node.IsCollection && node.RelationKey is null)
        {
            return node.Owner ?? node.PoolKey ?? node.TypeFullName;
        }

        var id = IdOf(record) ?? "";
        return node.RelationKey is null
            ? Scope(node.PoolKey ?? node.TypeFullName, id)
            : Scope(TypeOf(record, node.SeedType ?? node.TypeFullName), id);
    }

    private List<(string Type, string Id)> RelationRefs(SchemaRegistry registry, string relationKey, string targetType, bool collection)
    {
        if (!_relations.TryGetValue(relationKey, out var refs))
        {
            refs = SeedRelation(registry, relationKey, targetType, collection);
            _relations[relationKey] = refs;
        }

        return refs;
    }

    private IEnumerable<JsonObject> RelationTargets(SchemaRegistry registry, string relationKey, string targetType, bool collection)
    {
        foreach (var (type, id) in RelationRefs(registry, relationKey, targetType, collection))
        {
            if (GetOrSeedPool(registry, type).FindById(id) is { } target)
            {
                var clone = target.DeepClone().AsObject();
                clone["@odata.type"] = "#" + type;
                yield return clone;
            }
        }
    }

    // Deterministic: the same owner and navigation always get the same references (given the pools).
    private List<(string Type, string Id)> SeedRelation(SchemaRegistry registry, string relationKey, string targetType, bool collection)
    {
        var bar = relationKey.LastIndexOf('|');
        var navName = relationKey[(bar + 1)..];
        if (string.Equals(navName, "deletedItems", StringComparison.OrdinalIgnoreCase))
        {
            return []; // nothing is deleted in a fresh mock tenant
        }

        var ownerId = relationKey[..bar].Split('/')[^1];
        var homes = registry.EntitySets.Values.Select(t => ResolveRefName(registry, t))
            .Distinct(StringComparer.Ordinal)
            .Where(t => IsSameOrDerived(registry, t, targetType))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (RelationshipTargetByName.TryGetValue(navName, out var preferred) && homes.Contains(preferred))
        {
            homes = [preferred];
        }
        else if (homes.Contains(targetType))
        {
            homes = [targetType];
        }

        var candidates = homes
            .SelectMany(t => GetOrSeedPool(registry, t).Select(e => (Type: t, Id: IdOf(e))))
            .Where(c => c.Id is not null && c.Id != ownerId)
            .Select(c => (c.Type, Id: c.Id!))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var count = Math.Min(collection ? RelationshipSize : 1, candidates.Count);
        var start = (int)(StableHash(relationKey) % (uint)candidates.Count);
        return [.. Enumerable.Range(0, count).Select(i => candidates[(start + i) % candidates.Count])];
    }

    /// <summary>$ref on a relationship: GET lists references, POST/PUT add or set one, DELETE removes one.</summary>
    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleRelationRef(SchemaRegistry registry, ResolvedNode node, string method, MockRequest request, string versionSegment)
    {
        var relationKey = node.RelationKey!;
        var targetType = node.SeedType ?? node.TypeFullName;
        var isCollection = node.IsCollection || node.ItemId is not null;
        var refs = RelationRefs(registry, relationKey, targetType, isCollection);
        var navName = relationKey[(relationKey.LastIndexOf('|') + 1)..];
        string Link(string id) => $"{request.RequestUri.Scheme}://{request.RequestUri.Authority}{versionSegment}directoryObjects/{id}";

        if (node.ItemId is { } removeId)
        {
            if (method != "DELETE")
            {
                return (null, null);
            }

            return refs.RemoveAll(r => r.Id == removeId) > 0
                ? (null, HttpStatusCode.NoContent)
                : (ODataError("Request_ResourceNotFound", $"Resource '{removeId}' does not exist or one of its queried reference-property objects are not present."), HttpStatusCode.NotFound);
        }

        if (method == "GET")
        {
            return isCollection
                ? (new JsonObject { ["value"] = new JsonArray([.. refs.Select(r => (JsonNode)new JsonObject { ["@odata.id"] = Link(r.Id) })]) }, HttpStatusCode.OK)
                : refs.Count == 0
                    ? (ODataError("Request_ResourceNotFound", "Resource not found."), HttpStatusCode.NotFound)
                    : (new JsonObject { ["@odata.id"] = Link(refs[0].Id) }, HttpStatusCode.OK);
        }

        if (method == "DELETE" && !isCollection)
        {
            refs.Clear();
            return (null, HttpStatusCode.NoContent);
        }

        if (!(isCollection ? method == "POST" : method == "PUT"))
        {
            return (null, null);
        }

        var body = ParseBody(request);
        if (body?["@odata.id"] is not JsonValue idValue || !idValue.TryGetValue(out string? link) || string.IsNullOrWhiteSpace(link))
        {
            return (ODataError("BadRequest", "An @odata.id property is required in the request body."), HttpStatusCode.BadRequest);
        }

        var id = link.TrimEnd('/').Split('/')[^1];
        var type = registry.EntitySets.Values.Select(t => ResolveRefName(registry, t)).Distinct(StringComparer.Ordinal)
            .Where(t => IsSameOrDerived(registry, t, targetType))
            .FirstOrDefault(t => GetOrSeedPool(registry, t).FindById(id) is not null) ?? targetType;

        if (isCollection)
        {
            if (refs.Exists(r => r.Id == id))
            {
                return (ODataError("Request_BadRequest", $"One or more added object references already exist for the following modified properties: '{navName}'."), HttpStatusCode.BadRequest);
            }

            refs.Add((type, id));
        }
        else
        {
            refs.Clear();
            refs.Add((type, id));
        }

        return (null, HttpStatusCode.NoContent);
    }
}
