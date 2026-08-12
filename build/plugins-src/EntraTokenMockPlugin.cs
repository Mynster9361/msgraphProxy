// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

public sealed class EntraTokenMockPluginConfiguration
{
    /// <summary>App roles to include in the "roles" claim for client-credentials (app-only) tokens.</summary>
    public IEnumerable<string> Roles { get; set; } = [];
    /// <summary>Scopes to include in the "scp" claim for delegated tokens.</summary>
    public IEnumerable<string> Scopes { get; set; } = [];
}

/// <summary>
/// Mocks the Microsoft identity platform token endpoint
/// (login.microsoftonline.com/{tenant}/oauth2(/v2.0)/token) so tools like
/// MSAL-based auth modules can complete a client-credentials (or other) flow
/// entirely locally, without a real Entra app registration. The issued JWT
/// is locally self-signed - nothing validates its signature, since the real
/// Graph endpoint it would normally be sent to is itself being mocked.
/// </summary>
public sealed class EntraTokenMockPlugin(
    HttpClient httpClient,
    ILogger<EntraTokenMockPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<EntraTokenMockPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")));

    public override string Name => nameof(EntraTokenMockPlugin);

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.ShouldExecute(UrlsToWatch))
        {
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var path = request.RequestUri.AbsolutePath;
        if (!path.EndsWith("/token", StringComparison.OrdinalIgnoreCase) ||
            !path.Contains("/oauth2", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var form = System.Web.HttpUtility.ParseQueryString(request.BodyString ?? "");
        var clientId = form["client_id"];
        var tenantId = ExtractTenantId(path);
        var grantType = form["grant_type"];
        var isAppOnly = string.Equals(grantType, "client_credentials", StringComparison.OrdinalIgnoreCase);

        var accessToken = CreateAccessToken(clientId, tenantId, isAppOnly);

        var body = new JsonObject
        {
            ["token_type"] = "Bearer",
            ["expires_in"] = 3599,
            ["ext_expires_in"] = 3599,
            ["access_token"] = accessToken,
        };

        e.Session.GenericResponse(
            body.ToJsonString(),
            HttpStatusCode.OK,
            [new HttpHeader("Content-Type", "application/json")]);
        e.ResponseState.HasBeenSet = true;

        Logger.LogRequest("200 mocked Entra ID token", MessageType.Mocked, new LoggingContext(e.Session));

        return Task.CompletedTask;
    }

    private static string ExtractTenantId(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : "common";
    }

    private string CreateAccessToken(string? clientId, string tenantId, bool isAppOnly)
    {
        var subject = clientId ?? "00000000-0000-0000-0000-000000000000";
        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, subject));
        identity.AddClaim(new Claim("appid", subject));
        identity.AddClaim(new Claim("tid", tenantId));
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Aud, "https://graph.microsoft.com"));

        if (isAppOnly)
        {
            var roles = Configuration.Roles.Any() ? Configuration.Roles : ["User.Read.All"];
            foreach (var role in roles)
            {
                identity.AddClaim(new Claim("roles", role));
            }
        }
        else
        {
            var scopes = Configuration.Scopes.Any() ? Configuration.Scopes : ["User.Read"];
            identity.AddClaim(new Claim("scp", string.Join(' ', scopes)));
        }

        var handler = new JwtSecurityTokenHandler();
        var signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256Signature);
        var now = DateTime.UtcNow;
        var token = handler.CreateJwtSecurityToken(
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            audience: null,
            identity,
            notBefore: now,
            expires: now.AddHours(1),
            issuedAt: now,
            signingCredentials: signingCredentials);

        return handler.WriteToken(token);
    }
}
