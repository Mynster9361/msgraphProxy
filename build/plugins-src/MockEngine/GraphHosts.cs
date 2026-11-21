// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MsGraphProxy.MockEngine;

/// <summary>
/// Which hosts are Microsoft Graph. Replaces Dev Proxy's ProxyUtils.IsGraphUrl, which only matches hosts
/// starting with "graph.microsoft." or "microsoftgraph." and so misses dod-graph.microsoft.us: requests to it
/// were watched by the shipped configs but never mocked, so they leaked to the real DoD Graph endpoint.
/// </summary>
public sealed class GraphHosts
{
    /// <summary>Global, US Government L4, US Government L5 (DoD) and China (21Vianet).</summary>
    public static IReadOnlyList<string> BuiltIn { get; } =
    [
        "graph.microsoft.com",
        "graph.microsoft.us",
        "dod-graph.microsoft.us",
        "microsoftgraph.chinacloudapi.cn",
    ];

    private readonly HashSet<string> _hosts;

    /// <param name="additionalHosts">Extra Graph hosts, e.g. sovereign-cloud endpoints once verified.</param>
    public GraphHosts(IEnumerable<string>? additionalHosts = null)
    {
        _hosts = new HashSet<string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
        foreach (var host in additionalHosts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                _ = _hosts.Add(host.Trim());
            }
        }
    }

    public IReadOnlyCollection<string> Hosts => _hosts;

    public bool IsGraphUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Exact hosts first; the prefix rules keep Dev Proxy's original matching so nothing that
        // was mocked before stops being mocked.
        return _hosts.Contains(uri.Host) ||
            uri.Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.StartsWith("microsoftgraph.", StringComparison.OrdinalIgnoreCase);
    }
}
