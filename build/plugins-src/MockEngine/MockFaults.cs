// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MsGraphProxy.MockEngine;

/// <summary>Where a fault rule applies.</summary>
public enum GraphMockFaultScope
{
    Any,

    /// <summary>Only requests sent directly (not inside $batch). Includes the outer POST /$batch itself.</summary>
    DirectOnly,

    /// <summary>Only $batch sub-requests.</summary>
    BatchOnly,
}

/// <summary>Which requests share a throttle bucket.</summary>
public enum GraphMockBucketScope
{
    /// <summary>One bucket for everything matching it.</summary>
    Global,

    /// <summary>One bucket per calling app (keyed by the Authorization header).</summary>
    App,

    /// <summary>One bucket per mailbox (/users/{id}/messages..., /me/events...).</summary>
    Mailbox,
}

/// <summary>
/// One fault or latency rule. Paths are globs over the version-relative path ("/users/*/messages/**"):
/// "*" is one segment, "**" any number, matched case-insensitively.
/// </summary>
public sealed class GraphMockFaultRule
{
    public string? Name { get; set; }
    public string Path { get; set; } = "/**";

    /// <summary>HTTP methods; empty means any.</summary>
    public Collection<string> Methods { get; } = [];

    /// <summary>Substring the decoded query must contain, e.g. "$select=signInActivity".</summary>
    public string? QueryContains { get; set; }

    /// <summary>"Name" (present) or "Name=value" (case-insensitive value).</summary>
    public string? Header { get; set; }

    public GraphMockFaultScope Scope { get; set; } = GraphMockFaultScope.Any;

    /// <summary>Status to return. Null means a pure latency rule.</summary>
    public int? Status { get; set; }

    public string? Code { get; set; }
    public string? Message { get; set; }
    public int? RetryAfterSeconds { get; set; }

    /// <summary>Chance a matching request is failed, 0..1 (drawn from the seed when one is set).</summary>
    public double Probability { get; set; } = 1.0;

    /// <summary>Fail only every Nth matching request.</summary>
    public int? EveryNth { get; set; }

    /// <summary>Stop after this many failures.</summary>
    public int? MaxHits { get; set; }

    /// <summary>Delay before the request is processed (direct requests via HandleAsync only).</summary>
    public int? LatencyMs { get; set; }
}

/// <summary>A token bucket for one workload, e.g. Outlook: 10,000 requests per 10 minutes per mailbox.</summary>
public sealed class GraphMockThrottleBucket
{
    public string Name { get; set; } = "bucket";

    /// <summary>Path globs this bucket covers; empty means everything except /$batch.</summary>
    public Collection<string> Paths { get; } = [];

    public GraphMockBucketScope Scope { get; set; } = GraphMockBucketScope.Global;
    public double Capacity { get; set; } = 100;
    public double RefillPerSecond { get; set; } = 10;

    /// <summary>Retry-After sent when the bucket is empty. Null: the time until one token is back.</summary>
    public int? RetryAfterSeconds { get; set; }
}

/// <summary>Fault injection and throttling (msgraphProxy P0 #7). Everything is off until configured.</summary>
public sealed class GraphMockFaultOptions
{
    public Collection<GraphMockFaultRule> Rules { get; } = [];
    public Collection<GraphMockThrottleBucket> Buckets { get; } = [];

    /// <summary>Graph allows 4 concurrent requests per mailbox per app. Null disables the check.</summary>
    public int? MailboxConcurrencyLimit { get; set; }

    /// <summary>Retry-After for mailbox concurrency 429s.</summary>
    public int MailboxConcurrencyRetryAfterSeconds { get; set; } = 1;
}

/// <summary>What the mock has served so far; the basis for "0 Retry-After violations" style assertions.</summary>
public sealed record MockStats(
    long Requests,
    long BatchSubRequests,
    long NonGetRequests,
    long Throttled,
    long Faults,
    long RetryAfterViolations,
    IReadOnlyDictionary<string, long> RuleHits);

internal sealed record FaultDecision(int Status, string Code, string Message, int? RetryAfterSeconds);

/// <summary>Decides, per request, whether to fail it. All methods except the in-flight counters run under the engine lock.</summary>
internal sealed class FaultGate(GraphMockFaultOptions options, MockClock clock)
{
    private static readonly HashSet<string> MailboxNavigations = new(StringComparer.OrdinalIgnoreCase)
    {
        "messages", "mailFolders", "events", "calendar", "calendars", "calendarView", "calendarGroups",
        "contacts", "contactFolders", "outlook", "inferenceClassification", "mailboxSettings", "todo",
    };

    private readonly ConcurrentDictionary<string, int> _inFlightByMailbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GraphMockFaultRule, (long Matches, long Fired)> _ruleState = [];
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _ruleHits = new(StringComparer.Ordinal);
    private Dictionary<string, int>? _batchMailboxCounts;
    private long _requests;
    private long _subRequests;
    private long _nonGet;
    private long _throttled;
    private long _faults;
    private long _violations;

    public bool HasRules => options.Rules.Count > 0;

    public MockStats Stats() => new(_requests, _subRequests, _nonGet, _throttled, _faults, _violations, new Dictionary<string, long>(_ruleHits, StringComparer.Ordinal));

    public void Reset()
    {
        _ruleState.Clear();
        _buckets.Clear();
        _blockedUntil.Clear();
        _ruleHits.Clear();
        _requests = _subRequests = _nonGet = _throttled = _faults = _violations = 0;
    }

    // --- in-flight tracking (outside the engine lock) ------------------------------------------

    public void Enter(string? mailbox)
    {
        if (mailbox is not null)
        {
            _ = _inFlightByMailbox.AddOrUpdate(mailbox, 1, (_, n) => n + 1);
        }
    }

    public void Exit(string? mailbox)
    {
        if (mailbox is not null)
        {
            _ = _inFlightByMailbox.AddOrUpdate(mailbox, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    /// <summary>"users/{id}" or "me" for Outlook resources, else null. Path is version-relative.</summary>
    public static string? MailboxKeyFor(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && string.Equals(segments[0], "users", StringComparison.OrdinalIgnoreCase) && MailboxNavigations.Contains(StripParens(segments[2])))
        {
            return "users/" + segments[1].ToLowerInvariant();
        }

        if (segments.Length >= 2 && string.Equals(segments[0], "me", StringComparison.OrdinalIgnoreCase) && MailboxNavigations.Contains(StripParens(segments[1])))
        {
            return "me";
        }

        return null;
    }

    // --- batch scope ---------------------------------------------------------------------------

    /// <summary>Starts a $batch. Parallel batches count same-mailbox sub-requests as concurrent.</summary>
    public void BeginBatch(bool parallel) => _batchMailboxCounts = parallel ? new(StringComparer.OrdinalIgnoreCase) : null;

    public void EndBatch() => _batchMailboxCounts = null;

    // --- decisions -----------------------------------------------------------------------------

    /// <summary>Total latency of the pure-latency rules matching a direct request.</summary>
    public int LatencyFor(string method, string relativePath, string query, Func<string, string?> getHeader)
    {
        var total = 0;
        foreach (var rule in options.Rules)
        {
            if (rule.Status is null && rule.LatencyMs is > 0 && rule.Scope != GraphMockFaultScope.BatchOnly && Matches(rule, method, relativePath, query, getHeader))
            {
                total += rule.LatencyMs.Value;
            }
        }

        return total;
    }

    /// <summary>Null lets the request through; otherwise the error to return.</summary>
    public FaultDecision? Evaluate(string method, string relativePath, string query, Func<string, string?> getHeader, bool inBatch, out IReadOnlyList<KeyValuePair<string, string>>? warningHeaders)
    {
        warningHeaders = null;
        var isOuterBatch = !inBatch && string.Equals(relativePath.Trim('/'), "$batch", StringComparison.OrdinalIgnoreCase);
        _requests += inBatch ? 0 : 1;
        _subRequests += inBatch ? 1 : 0;
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !isOuterBatch)
        {
            _nonGet++;
        }

        var now = clock.GetUtcNow();
        var mailbox = MailboxKeyFor(relativePath);
        var app = getHeader("Authorization") ?? "anonymous";

        // 1. Retry-After enforcement: a request on a key that's still blocked is a violation.
        foreach (var key in BlockKeysFor(relativePath, mailbox, app))
        {
            if (_blockedUntil.TryGetValue(key, out var until) && now < until)
            {
                _violations++;
                return Throttle("TooManyRequests", "Retry-After was not honored; the client retried before it elapsed.", SecondsUntil(until, now), key, now, alreadyBlocked: true);
            }
        }

        // 2. Fault rules (first one that fires wins).
        foreach (var rule in options.Rules)
        {
            if (rule.Status is null || !AppliesTo(rule, inBatch) || !Matches(rule, method, relativePath, query, getHeader))
            {
                continue;
            }

            var (matches, fired) = _ruleState.GetValueOrDefault(rule);
            matches++;
            var fire = (rule.EveryNth is not { } nth || nth <= 1 || matches % nth == 0) &&
                       (rule.MaxHits is not { } max || fired < max) &&
                       (rule.Probability >= 1.0 || NextDouble("fault:" + RuleName(rule)) < rule.Probability);
            if (fire)
            {
                fired++;
            }

            _ruleState[rule] = (matches, fired);
            if (!fire)
            {
                continue;
            }

            var name = RuleName(rule);
            _ruleHits[name] = _ruleHits.GetValueOrDefault(name) + 1;
            _faults++;
            var status = rule.Status.Value;
            if (rule.RetryAfterSeconds is { } retry)
            {
                _blockedUntil["rule:" + name + "|" + (mailbox ?? "")] = now.AddSeconds(retry);
            }

            if (status == 429)
            {
                _throttled++;
            }

            return new FaultDecision(status, rule.Code ?? DefaultCode(status), rule.Message ?? DefaultMessage(status), rule.RetryAfterSeconds);
        }

        if (isOuterBatch)
        {
            return null; // sub-requests are throttled individually
        }

        // 3. Mailbox concurrency: Graph allows 4 concurrent requests per mailbox per app.
        if (mailbox is not null && options.MailboxConcurrencyLimit is { } limit)
        {
            var concurrent = _inFlightByMailbox.GetValueOrDefault(mailbox);
            if (inBatch && _batchMailboxCounts is not null)
            {
                _batchMailboxCounts[mailbox] = _batchMailboxCounts.GetValueOrDefault(mailbox) + 1;
                concurrent += _batchMailboxCounts[mailbox];
            }

            if (concurrent > limit)
            {
                _throttled++;
                return new FaultDecision(
                    429,
                    "ApplicationThrottled",
                    $"Application is over its MailboxConcurrency limit ({limit.ToString(CultureInfo.InvariantCulture)}).",
                    options.MailboxConcurrencyRetryAfterSeconds);
            }
        }

        // 4. Token buckets.
        List<KeyValuePair<string, string>>? warnings = null;
        foreach (var bucket in options.Buckets)
        {
            if (!BucketCovers(bucket, relativePath))
            {
                continue;
            }

            var key = bucket.Scope switch
            {
                GraphMockBucketScope.Mailbox when mailbox is null => null,
                GraphMockBucketScope.Mailbox => "bucket:" + bucket.Name + "|" + mailbox,
                GraphMockBucketScope.App => "bucket:" + bucket.Name + "|" + app,
                _ => "bucket:" + bucket.Name,
            };
            if (key is null)
            {
                continue;
            }

            var (tokens, at) = _buckets.TryGetValue(key, out var state) ? state : (bucket.Capacity, now);
            tokens = Math.Min(bucket.Capacity, tokens + (Math.Max(0, (now - at).TotalSeconds) * bucket.RefillPerSecond));
            if (tokens < 1)
            {
                _buckets[key] = (tokens, now);
                var retry = bucket.RetryAfterSeconds ?? (int)Math.Ceiling((1 - tokens) / Math.Max(bucket.RefillPerSecond, 0.001));
                return Throttle("TooManyRequests", $"Too many requests for '{bucket.Name}'.", Math.Max(1, retry), key, now, alreadyBlocked: false);
            }

            tokens -= 1;
            _buckets[key] = (tokens, now);
            var used = 1 - (tokens / bucket.Capacity);
            if (used >= 0.8)
            {
                warnings ??= [];
                warnings.Add(new("x-ms-throttle-limit-percentage", Math.Round(used, 2).ToString("0.00", CultureInfo.InvariantCulture)));
            }
        }

        warningHeaders = warnings;
        return null;
    }

    private FaultDecision Throttle(string code, string message, int retryAfter, string key, DateTimeOffset now, bool alreadyBlocked)
    {
        _throttled++;
        if (!alreadyBlocked)
        {
            _blockedUntil[key] = now.AddSeconds(retryAfter);
        }

        return new FaultDecision(429, code, message, retryAfter);
    }

    private IEnumerable<string> BlockKeysFor(string relativePath, string? mailbox, string app)
    {
        foreach (var rule in options.Rules)
        {
            if (rule.RetryAfterSeconds is not null && Glob(rule.Path).IsMatch(relativePath))
            {
                yield return "rule:" + RuleName(rule) + "|" + (mailbox ?? "");
            }
        }

        foreach (var bucket in options.Buckets)
        {
            if (!BucketCovers(bucket, relativePath))
            {
                continue;
            }

            yield return bucket.Scope switch
            {
                GraphMockBucketScope.Mailbox => "bucket:" + bucket.Name + "|" + (mailbox ?? "-"),
                GraphMockBucketScope.App => "bucket:" + bucket.Name + "|" + app,
                _ => "bucket:" + bucket.Name,
            };
        }
    }

    private static bool BucketCovers(GraphMockThrottleBucket bucket, string relativePath) =>
        !string.Equals(relativePath.Trim('/'), "$batch", StringComparison.OrdinalIgnoreCase) &&
        (bucket.Paths.Count == 0 || bucket.Paths.Any(p => Glob(p).IsMatch(relativePath)));

    private static bool AppliesTo(GraphMockFaultRule rule, bool inBatch) => rule.Scope switch
    {
        GraphMockFaultScope.DirectOnly => !inBatch,
        GraphMockFaultScope.BatchOnly => inBatch,
        _ => true,
    };

    private static bool Matches(GraphMockFaultRule rule, string method, string relativePath, string query, Func<string, string?> getHeader)
    {
        if (rule.Methods.Count > 0 && !rule.Methods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!Glob(rule.Path).IsMatch(relativePath))
        {
            return false;
        }

        if (rule.QueryContains is { } needle && !Uri.UnescapeDataString(query).Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.Header is { } header)
        {
            var eq = header.IndexOf('=', StringComparison.Ordinal);
            var name = eq >= 0 ? header[..eq].Trim() : header.Trim();
            var value = getHeader(name);
            if (value is null || (eq >= 0 && !string.Equals(value, header[(eq + 1)..].Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static double NextDouble(string scope)
    {
        // Bytes 8..15: in a version-8 GUID the fixed version nibble sits in byte 7, which would be the
        // most significant byte of bytes 0..7 and pin every draw to >= 0.5. Here the fixed variant
        // bits land in the least significant byte, which the >> 11 drops.
        var bytes = MockAmbient.NewGuid(scope).ToByteArray();
        return (BitConverter.ToUInt64(bytes, 8) >> 11) * (1.0 / (1UL << 53));
    }

    private static string RuleName(GraphMockFaultRule rule) => rule.Name ?? rule.Path;

    private static int SecondsUntil(DateTimeOffset until, DateTimeOffset now) => Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));

    private static string StripParens(string segment)
    {
        var paren = segment.IndexOf('(', StringComparison.Ordinal);
        return paren >= 0 ? segment[..paren] : segment;
    }

    private static string DefaultCode(int status) => status switch
    {
        400 => "BadRequest",
        401 => "InvalidAuthenticationToken",
        403 => "Authorization_RequestDenied",
        404 => "Request_ResourceNotFound",
        429 => "TooManyRequests",
        500 => "InternalServerError",
        501 => "NotImplemented",
        503 => "ServiceUnavailable",
        504 => "GatewayTimeout",
        _ => "UnknownError",
    };

    private static string DefaultMessage(int status) => status switch
    {
        403 => "Insufficient privileges to complete the operation.",
        429 => "Too many requests.",
        503 => "The service is temporarily unavailable.",
        504 => "The request timed out.",
        _ => $"Injected fault ({status.ToString(CultureInfo.InvariantCulture)}).",
    };

    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new(StringComparer.Ordinal);

    // "*" = one segment, "**" = any number of segments (including none).
    internal static Regex Glob(string glob) => GlobCache.GetOrAdd(glob, static g =>
    {
        var parts = g.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pattern = "^";
        foreach (var part in parts)
        {
            pattern += part switch
            {
                "**" => "(?:/[^/]+)*",
                "*" => "/[^/]+",
                _ => "/" + Regex.Escape(part).Replace(@"\*", "[^/]*", StringComparison.Ordinal),
            };
        }

        return new Regex(pattern + "/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    });
}
