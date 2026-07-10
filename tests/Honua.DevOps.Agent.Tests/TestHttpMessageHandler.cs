using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Honua.DevOps.Agent.Tests;

internal sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    internal List<CapturedRequest> CapturedRequests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        CapturedRequests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri?.AbsoluteUri ?? string.Empty,
            body)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme,
            AuthorizationParameter = request.Headers.Authorization?.Parameter,
            ApiKey = request.Headers.TryGetValues("X-API-Key", out IEnumerable<string>? apiKeys)
                ? apiKeys.SingleOrDefault()
                : null,
            McpSessionId = request.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? sessionIds)
                ? sessionIds.SingleOrDefault()
                : null
        });

        return responder(request);
    }

    internal static HttpResponseMessage JsonOk(object payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload)
        };
    }
}

internal sealed record CapturedRequest(
    string Method,
    string Uri,
    string? Body)
{
    internal string? AuthorizationScheme { get; init; }
    internal string? AuthorizationParameter { get; init; }

    internal string? ApiKey { get; init; }

    internal string? McpSessionId { get; init; }
}
