// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MsGraphProxy.MockEngine;

/// <summary>
/// msgraphProxy P2 #14: delta queries backed by a change log that every mutation feeds.
/// <list type="bullet">
/// <item>GET {collection}/delta without a token pages through everything and ends with an @odata.deltaLink;
/// with $deltatoken=latest it returns no items, only a deltaLink for "now".</item>
/// <item>A token round returns what changed since the token: the current item for creates and updates, and
/// {"id", "@removed": {"reason"}} for deletes ("changed" for directory objects, which are soft-deleted;
/// "deleted" otherwise, e.g. a message moved out of a folder).</item>
/// <item>Groups carry "members@delta": every member in a full round, only added and removed members afterwards.</item>
/// <item><see cref="ExpireDeltaTokens"/> invalidates every token handed out so far: the next round gets 410 Gone
/// (resyncRequired for directory objects, syncStateNotFound otherwise).</item>
/// </list>
/// Delta works on root sets and on contained collections (per folder: /users/{id}/mailFolders/{id}/messages/delta).
/// </summary>
public sealed partial class GraphMockEngine
{
    /// <summary>Items per delta page unless the request sets Prefer: odata.maxpagesize.</summary>
    public int DeltaPageSize { get; init; } = 100;

    private static readonly HashSet<string> SoftDeletedTypes = new(StringComparer.Ordinal)
    {
        "microsoft.graph.user", "microsoft.graph.group", "microsoft.graph.application", "microsoft.graph.servicePrincipal",
        "microsoft.graph.device", "microsoft.graph.orgContact", "microsoft.graph.administrativeUnit",
    };

    private readonly List<DeltaChange> _changes = [];
    private long _deltaSeq;
    private long _deltaEpoch;

    private sealed record DeltaChange(long Seq, string Key, string Id, bool Removed, string? Reason = null, string? MemberType = null);

    /// <summary>Invalidates every delta token handed out so far (Graph's 410 resync).</summary>
    public void ExpireDeltaTokens()
    {
        lock (_gate)
        {
            _deltaEpoch++;
        }
    }

    private void ResetDelta()
    {
        _changes.Clear();
        _deltaSeq = 0;
        _deltaEpoch = 0;
    }

    private void RecordUpsert(string poolKey, string? id)
    {
        if (id is not null)
        {
            _changes.Add(new DeltaChange(++_deltaSeq, poolKey, id, Removed: false));
        }
    }

    private void RecordRemoved(string poolKey, string typeFullName, string? id)
    {
        if (id is not null)
        {
            _changes.Add(new DeltaChange(++_deltaSeq, poolKey, id, Removed: true, SoftDeletedTypes.Contains(typeFullName) ? "changed" : "deleted"));
        }
    }

    /// <summary>A $ref change on a relationship ("{owner scope}|{navigation}").</summary>
    private void RecordRelation(string relationKey, string memberType, string memberId, bool added) =>
        _changes.Add(new DeltaChange(++_deltaSeq, relationKey, memberId, Removed: !added, MemberType: memberType));

    private static string PoolKeyOf(ResolvedNode node) => node.PoolKey ?? node.SeedType ?? node.TypeFullName;

    private static readonly Dictionary<string, string> FolderNavigations = new(StringComparer.Ordinal)
    {
        ["messages"] = "mailFolders",
        ["events"] = "calendars",
        ["contacts"] = "contactFolders",
    };

    /// <summary>A record in one of the owner's folder pools ("{owner}/mailFolders/{id}/messages") for "{owner}/messages".</summary>
    private JsonObject? FindInMailboxFolders(string? poolKey, string id)
    {
        if (poolKey is null)
        {
            return null;
        }

        var slash = poolKey.LastIndexOf('/');
        if (slash < 0 || !FolderNavigations.TryGetValue(poolKey[(slash + 1)..], out var folders))
        {
            return null;
        }

        var prefix = poolKey[..slash] + "/" + folders + "/";
        var suffix = poolKey[slash..];
        foreach (var (key, folderPool) in _store.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal) && folderPool.FindById(id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A deleted directory object leaves every relationship (group membership, manager, owners), like Graph, and each
    /// removal shows up in the owner's members@delta.
    /// </summary>
    private void RemoveFromRelationships(string typeFullName, string id)
    {
        if (!SoftDeletedTypes.Contains(typeFullName))
        {
            return;
        }

        foreach (var (key, refs) in _relations.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            foreach (var r in refs.Where(r => r.Id == id).ToList())
            {
                RecordRelation(key, r.Type, r.Id, added: false);
            }

            _ = refs.RemoveAll(r => r.Id == id);
        }
    }

    private (JsonNode? Body, HttpStatusCode? StatusCode) HandleDelta(SchemaRegistry registry, string versionSegment, ResolvedNode node, MockRequest request)
    {
        var typeFullName = node.SeedType ?? node.TypeFullName;
        var poolKey = PoolKeyOf(node);
        var pool = GetOrSeedPool(registry, typeFullName, node.PoolKey);
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var isDirectory = SoftDeletedTypes.Contains(typeFullName);

        long from;
        long to;
        var offset = 0;
        if (query["$skiptoken"] is { } skip)
        {
            if (!TryDecodeDeltaToken(skip, out var t) || t.Epoch != _deltaEpoch)
            {
                return Gone(isDirectory);
            }

            (from, to, offset) = (t.From, t.To, t.Offset);
        }
        else if (query["$deltatoken"] is { } token)
        {
            if (string.Equals(token, "latest", StringComparison.OrdinalIgnoreCase))
            {
                return (Page(versionSegment, request, [], next: null, delta: DeltaLink(request, _deltaSeq)), HttpStatusCode.OK);
            }

            if (!TryDecodeDeltaToken(token, out var t) || t.Epoch != _deltaEpoch)
            {
                return Gone(isDirectory);
            }

            (from, to) = (t.To, _deltaSeq);
        }
        else
        {
            (from, to) = (-1, _deltaSeq); // a full round: everything, then a token for the end of it
        }

        var withMembers = string.Equals(typeFullName, "microsoft.graph.group", StringComparison.Ordinal)
            && (query["$select"] is not { } select || SplitSelect(select).Contains("members", StringComparer.OrdinalIgnoreCase));
        var entries = from < 0
            ? FullRound(registry, typeFullName, poolKey, pool, request, withMembers)
            : ChangesSince(registry, typeFullName, poolKey, pool, request, withMembers, from, to);

        var size = TryGetMaxPageSizePreference(request) ?? DeltaPageSize;
        var page = entries.Skip(offset).Take(size).ToList();
        var more = offset + size < entries.Count;
        var body = Page(versionSegment, request, page,
            next: more ? DeltaPageLink(request, new DeltaToken(_deltaEpoch, from, to, offset + size)) : null,
            delta: more ? null : DeltaLink(request, to));
        return (body, HttpStatusCode.OK);
    }

    private List<JsonObject> FullRound(SchemaRegistry registry, string typeFullName, string poolKey, EntityPool pool, MockRequest request, bool withMembers)
    {
        var entries = new List<JsonObject>();
        foreach (var item in pool)
        {
            var entry = TrimTo(item, GetSelectedProps(registry, typeFullName, request));
            CopyODataType(item, entry);
            if (withMembers && IdOf(item) is { } id)
            {
                entry["members@delta"] = new JsonArray([.. RelationRefs(registry, Scope(poolKey, id) + "|members", "microsoft.graph.directoryObject", collection: true)
                    .Select(m => (JsonNode)new JsonObject { ["@odata.type"] = "#" + m.Type, ["id"] = m.Id })]);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private List<JsonObject> ChangesSince(SchemaRegistry registry, string typeFullName, string poolKey, EntityPool pool, MockRequest request, bool withMembers, long from, long to)
    {
        var window = _changes.Where(c => c.Seq > from && c.Seq <= to).ToList();

        // Items: the last change per id wins. Membership: the last change per (group, member) wins.
        var items = window.Where(c => c.Key == poolKey).GroupBy(c => c.Id).Select(g => g.Last()).OrderBy(c => c.Seq).ToList();
        var memberships = new Dictionary<string, List<DeltaChange>>(StringComparer.Ordinal);
        if (withMembers)
        {
            var prefix = poolKey + "/";
            foreach (var g in window.Where(c => c.MemberType is not null && c.Key.StartsWith(prefix, StringComparison.Ordinal) && c.Key.EndsWith("|members", StringComparison.Ordinal))
                .GroupBy(c => (Group: c.Key[prefix.Length..c.Key.LastIndexOf('|')], c.Id)))
            {
                if (!memberships.TryGetValue(g.Key.Group, out var list))
                {
                    memberships[g.Key.Group] = list = [];
                }

                list.Add(g.Last());
            }
        }

        var entries = new List<JsonObject>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in items)
        {
            emitted.Add(change.Id);
            if (change.Removed || pool.FindById(change.Id) is not { } current)
            {
                entries.Add(new JsonObject { ["id"] = change.Id, ["@removed"] = new JsonObject { ["reason"] = change.Reason ?? "deleted" } });
                continue;
            }

            entries.Add(Entry(registry, typeFullName, current, request, memberships.GetValueOrDefault(change.Id)));
        }

        foreach (var (groupId, changes) in memberships.OrderBy(m => m.Value.Min(c => c.Seq)))
        {
            if (!emitted.Contains(groupId) && pool.FindById(groupId) is { } group)
            {
                entries.Add(Entry(registry, typeFullName, group, request, changes));
            }
        }

        return entries;
    }

    private static JsonObject Entry(SchemaRegistry registry, string typeFullName, JsonObject current, MockRequest request, List<DeltaChange>? memberChanges)
    {
        var entry = TrimTo(current, GetSelectedProps(registry, typeFullName, request));
        CopyODataType(current, entry);
        if (memberChanges is { Count: > 0 })
        {
            entry["members@delta"] = new JsonArray([.. memberChanges.Select(c =>
            {
                var member = new JsonObject { ["@odata.type"] = "#" + c.MemberType, ["id"] = c.Id };
                if (c.Removed)
                {
                    member["@removed"] = new JsonObject { ["reason"] = "deleted" };
                }

                return (JsonNode)member;
            })]);
        }

        return entry;
    }

    private static JsonObject Page(string versionSegment, MockRequest request, List<JsonObject> items, string? next, string? delta)
    {
        var body = new JsonObject
        {
            ["@odata.context"] = BuildODataContext(versionSegment, request),
            ["value"] = new JsonArray([.. items.Select(i => (JsonNode)i)]),
        };
        if (next is not null)
        {
            body["@odata.nextLink"] = next;
        }

        if (delta is not null)
        {
            body["@odata.deltaLink"] = delta;
        }

        return body;
    }

    private static (JsonNode? Body, HttpStatusCode? StatusCode) Gone(bool directory) => directory
        ? (ODataError("resyncRequired", "Resync required. Replace any local cached data with a full delta round."), HttpStatusCode.Gone)
        : (ODataError("syncStateNotFound", "The sync state generation is not found. Start a new sync."), HttpStatusCode.Gone);

    private readonly record struct DeltaToken(long Epoch, long From, long To, int Offset);

    private string DeltaLink(MockRequest request, long seq) =>
        LinkWith(request, "$deltatoken", EncodeDeltaToken(new DeltaToken(_deltaEpoch, seq, seq, 0)));

    private static string DeltaPageLink(MockRequest request, DeltaToken token) => LinkWith(request, "$skiptoken", EncodeDeltaToken(token));

    /// <summary>The request's own path and query options ($select, ...), with $deltatoken/$skiptoken replaced.</summary>
    private static string LinkWith(MockRequest request, string name, string value)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        query.Remove("$deltatoken");
        query.Remove("$skiptoken");
        var parts = new List<string>();
        foreach (var key in query.AllKeys.Where(k => k is not null))
        {
            foreach (var v in query.GetValues(key!) ?? [])
            {
                parts.Add(Uri.EscapeDataString(key!).Replace("%24", "$", StringComparison.Ordinal) + "=" + Uri.EscapeDataString(v));
            }
        }

        parts.Add(name + "=" + value);
        return $"{request.RequestUri.Scheme}://{request.RequestUri.Authority}{request.RequestUri.AbsolutePath}?{string.Join('&', parts)}";
    }

    private static string EncodeDeltaToken(DeltaToken t)
    {
        var json = new JsonObject { ["e"] = t.Epoch, ["f"] = t.From, ["t"] = t.To, ["o"] = t.Offset }.ToJsonString();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeDeltaToken(string token, out DeltaToken result)
    {
        result = default;
        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            if (JsonNode.Parse(Convert.FromBase64String(base64)) is not JsonObject node
                || node["e"] is not JsonValue e || !e.TryGetValue(out long epoch)
                || node["f"] is not JsonValue f || !f.TryGetValue(out long from)
                || node["t"] is not JsonValue t || !t.TryGetValue(out long to)
                || node["o"] is not JsonValue o || !o.TryGetValue(out int offset))
            {
                return false;
            }

            result = new DeltaToken(epoch, from, to, offset);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}
