// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MsGraphProxy.MockEngine;
using System.Net;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

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
    // Strict mode: a Graph request this plugin can't answer gets a 404/405 Graph error instead of
    // falling through to the real service, so a test run can never reach a real tenant.
    public bool Strict { get; set; }
    // Graph hosts beyond the built-in ones (graph.microsoft.com/.us, dod-graph.microsoft.us,
    // microsoftgraph.chinacloudapi.cn), e.g. sovereign-cloud endpoints. They must also be in urlsToWatch.
    public IEnumerable<string> AdditionalGraphHosts { get; set; } = [];
    // Page sizes ($top / Prefer: odata.maxpagesize / defaults, per type) and $skiptoken consistency
    // ("Snapshot" freezes the result set on page 1, "Offset" re-evaluates every page).
    public GraphMockPagingOptions Paging { get; set; } = new();
}

/// <summary>
/// Dev Proxy v3 adapter for <see cref="GraphMockEngine"/>: loads the configured CSDL files,
/// hands every intercepted Graph request to the engine, and writes back whatever it returns.
/// All mock behavior lives in the engine (MockEngine/GraphMockEngine.cs), which has no Dev Proxy
/// dependencies, so it can also run in-process or behind a later Dev Proxy v4 adapter.
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
    private GraphMockEngine? _engine;
    private GraphHosts _graphHosts = new();

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

        var v1Csdl = await File.ReadAllTextAsync(schemaFilePath, cancellationToken);

        string? betaCsdl = null;
        string? betaSchemaFilePath = null;
        if (!string.IsNullOrWhiteSpace(Configuration.BetaSchemaFilePath))
        {
            betaSchemaFilePath = ProxyUtils.GetFullPath(Configuration.BetaSchemaFilePath, ProxyConfiguration.ConfigFile);
            if (File.Exists(betaSchemaFilePath))
            {
                betaCsdl = await File.ReadAllTextAsync(betaSchemaFilePath, cancellationToken);
            }
            else
            {
                Logger.LogWarning("Beta Graph schema file '{BetaSchemaFilePath}' does not exist - /beta/* requests will not be mocked.", betaSchemaFilePath);
            }
        }

        _engine = new GraphMockEngine(v1Csdl, betaCsdl, Configuration.SubscribedSkus, Configuration.Paging);
        _graphHosts = new GraphHosts(Configuration.AdditionalGraphHosts);
        LogRegistryLoaded("v1.0", schemaFilePath);
        if (betaCsdl is not null)
        {
            LogRegistryLoaded("beta", betaSchemaFilePath!);
        }
    }

    private void LogRegistryLoaded(string version, string schemaFilePath)
    {
        if (_engine is null || !Logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var stats = _engine.GetSchemaStats(version);
        Logger.LogInformation(
            "{Name} loaded {EntityCount} entity types, {EnumCount} enums, {EntitySetCount} entity sets and {SingletonCount} singletons for {Version} from {SchemaFilePath}",
            Name,
            stats.EntityTypes,
            stats.Enums,
            stats.EntitySets,
            stats.Singletons,
            version,
            schemaFilePath);
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_engine is null || !e.ShouldExecute(UrlsToWatch))
        {
            return Task.CompletedTask;
        }

        var rawRequest = e.Session.HttpClient.Request;
        if (!_graphHosts.IsGraphUrl(rawRequest.RequestUri))
        {
            return Task.CompletedTask;
        }

        var method = rawRequest.Method?.ToUpperInvariant() ?? "";
        if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE") && !Configuration.Strict)
        {
            return Task.CompletedTask;
        }

        var request = new MockRequest(
            rawRequest.RequestUri,
            name => rawRequest.Headers.GetFirstHeader(name)?.Value,
            rawRequest.HasBody ? rawRequest.BodyString : null);

        var outcome = _engine.Handle(method, request);
        var response = outcome.Response;
        if (response is null)
        {
            if (Configuration.Strict)
            {
                response = GraphMockEngine.StrictResponse(outcome.SkipReason!.Value, request);
            }
            else
            {
                if (outcome.SkipReason == MockSkipReason.NoSchemaMatch)
                {
                    Logger.LogRequest("No schema match for this path", MessageType.Skipped, new LoggingContext(e.Session));
                }

                return Task.CompletedTask;
            }
        }

        e.Session.GenericResponse(
            response.Body,
            (HttpStatusCode)response.StatusCode,
            response.Headers.Select(h => new HttpHeader(h.Key, h.Value)));
        e.ResponseState.HasBeenSet = true;
        Logger.LogRequest($"{response.StatusCode} schema mock ({response.LogLabel})", MessageType.Mocked, new LoggingContext(e.Session));

        return Task.CompletedTask;
    }
}
