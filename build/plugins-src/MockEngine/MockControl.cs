// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MsGraphProxy.MockEngine;

/// <summary>
/// The control endpoint (msgraphProxy P0 #8), on any Graph host under /_msgraphproxy/:
/// <list type="bullet">
/// <item>GET  stats: requests, sub-requests, non-GETs, 429s, faults per rule, Retry-After violations.</item>
/// <item>POST reset [{"seed": n}]: fresh pools, snapshots, fault state, counters and clock; optionally reseed.</item>
/// <item>POST clock/advance {"seconds": n}: moves a fixed clock forward.</item>
/// <item>POST clock {"now": "2026-01-01T00:00:00Z"} (or null for the real clock).</item>
/// <item>GET / PUT scenario: the fault rules, buckets and mailbox limit, replaced as a whole.</item>
/// </list>
/// Control calls are not counted in stats and never throttled.
/// </summary>
internal static class MockControl
{
    public const string Prefix = "/_msgraphproxy/";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static bool IsControl(Uri uri) =>
        uri.AbsolutePath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.AbsolutePath.TrimEnd('/'), Prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static string Route(Uri uri) => uri.AbsolutePath.Length > Prefix.Length
        ? uri.AbsolutePath[Prefix.Length..].Trim('/').ToLowerInvariant()
        : "";

    public static MockResponse Ok(object value) => Respond(200, JsonSerializer.Serialize(value, Json));

    public static MockResponse Error(int status, string message) =>
        Respond(status, new JsonObject { ["error"] = new JsonObject { ["code"] = "MockControl", ["message"] = message } }.ToJsonString());

    public static JsonObject? ParseBody(MockRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(request.Body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MockResponse Respond(int status, string body) =>
        new(status, [new("Content-Type", "application/json")], body, "control");
}
