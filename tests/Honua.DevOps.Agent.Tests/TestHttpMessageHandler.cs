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
            request.RequestUri?.ToString() ?? string.Empty,
            body));

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
    string? Body);
