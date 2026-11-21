// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MsGraphProxy.MockEngine;

/// <summary>
/// Reproducibility settings (msgraphProxy P0 #3). With a <see cref="Seed"/>, the same seed and the same
/// request sequence give byte-identical responses: ids, generated GUIDs, dates, request-ids and
/// $skiptokens all come from the seed and a fixed or advanceable clock instead of randomness.
/// </summary>
public sealed class GraphMockDeterminismOptions
{
    /// <summary>Null keeps the historical behavior: random GUIDs on every run.</summary>
    public int? Seed { get; set; }

    /// <summary>"Now" for generated dates and the Date header. Null uses the real clock.</summary>
    public DateTimeOffset? FixedNow { get; set; }

    /// <summary>Records seeded per type on first access.</summary>
    public int DefaultPoolSize { get; set; } = 2;

    /// <summary>Per-type pool sizes keyed by full type name, e.g. "microsoft.graph.user": 500.</summary>
    public Dictionary<string, int> PoolSizes { get; } = new(StringComparer.Ordinal);

    internal int PoolSizeFor(string typeFullName) =>
        Math.Max(1, PoolSizes.TryGetValue(typeFullName, out var size) ? size : DefaultPoolSize);
}

/// <summary>
/// The engine's clock: the real clock, or a fixed "now" that only moves when <see cref="Advance"/> is called
/// (the hook for the control endpoint's "advance the clock", P0 #8).
/// </summary>
public sealed class MockClock : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _now;

    public MockClock(DateTimeOffset? fixedNow = null) => _now = fixedNow?.ToUniversalTime();

    public bool IsFixed
    {
        get
        {
            lock (_gate)
            {
                return _now is not null;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now ?? System.GetUtcNow();
        }
    }

    /// <summary>Fixes the clock at <paramref name="now"/>, or returns to the real clock when null.</summary>
    public void Set(DateTimeOffset? now)
    {
        lock (_gate)
        {
            _now = now?.ToUniversalTime();
        }
    }

    /// <summary>Moves a fixed clock forward. Has no effect on the real clock.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            if (_now is { } now)
            {
                _now = now + by;
            }
        }
    }
}

/// <summary>Deterministic GUIDs: SHA-256 over (seed, name), shaped as an RFC 9562 version 8 UUID.</summary>
internal sealed class MockEntropy(int? seed)
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public bool IsSeeded => seed is not null;

    /// <summary>The next GUID in a named sequence ("post:microsoft.graph.group", "request", ...).</summary>
    public Guid Next(string scope)
    {
        if (seed is null)
        {
            return Guid.NewGuid();
        }

        var n = _counters.TryGetValue(scope, out var current) ? current + 1 : 1;
        _counters[scope] = n;
        return Named(scope + "#" + n.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A GUID that depends only on the seed and the name, e.g. "id:microsoft.graph.user#3".</summary>
    public Guid Named(string name)
    {
        if (seed is null)
        {
            return Guid.NewGuid();
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed.Value.ToString(CultureInfo.InvariantCulture) + "|" + name), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}

/// <summary>
/// The entropy and clock of the engine currently handling a request on this thread. The fabrication
/// helpers are static and deeply nested, so the engine sets this for the duration of each (locked,
/// synchronous) call instead of threading a context parameter through every helper.
/// </summary>
internal static class MockAmbient
{
    [ThreadStatic]
    private static MockEntropy? t_entropy;

    [ThreadStatic]
    private static TimeProvider? t_clock;

    public static Guid NewGuid(string scope) => t_entropy?.Next(scope) ?? Guid.NewGuid();

    public static DateTimeOffset UtcNow => (t_clock ?? TimeProvider.System).GetUtcNow();

    public static Scope Enter(MockEntropy entropy, TimeProvider clock)
    {
        var previous = new Scope(t_entropy, t_clock);
        t_entropy = entropy;
        t_clock = clock;
        return previous;
    }

    internal readonly struct Scope(MockEntropy? entropy, TimeProvider? clock) : IDisposable
    {
        public void Dispose()
        {
            t_entropy = entropy;
            t_clock = clock;
        }
    }
}
