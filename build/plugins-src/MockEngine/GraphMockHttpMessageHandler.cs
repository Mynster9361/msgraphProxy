// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;

namespace MsGraphProxy.MockEngine;

/// <summary>
/// Runs <see cref="GraphMockEngine"/> in-process: plug it into an <see cref="HttpClient"/> and every
/// request is answered by the engine without a proxy, certificates or network.
/// Requests the engine doesn't handle get the strict-mode 404/405; nothing is ever forwarded.
/// </summary>
public sealed class GraphMockHttpMessageHandler(GraphMockEngine engine) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null)
        {
            throw new ArgumentException("The request has no RequestUri.", nameof(request));
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var mockRequest = new MockRequest(request.RequestUri, name => GetHeader(request, name), string.IsNullOrEmpty(body) ? null : body);

        // The engine is thread-safe, and the handler is always strict: nothing is ever forwarded.
        var outcome = await engine.HandleAsync(request.Method.Method, mockRequest, cancellationToken).ConfigureAwait(false);
        var response = outcome.Response ?? engine.StrictResponse(outcome.SkipReason!.Value, mockRequest);

        var message = new HttpResponseMessage((HttpStatusCode)response.StatusCode)
        {
            RequestMessage = request,
            Content = new StringContent(response.Body, Encoding.UTF8),
        };
        foreach (var (name, value) in response.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(value);
            }
            else if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static string? GetHeader(HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values) ||
            (request.Content is not null && request.Content.Headers.TryGetValues(name, out values)))
        {
            return values.FirstOrDefault();
        }

        return null;
    }
}
