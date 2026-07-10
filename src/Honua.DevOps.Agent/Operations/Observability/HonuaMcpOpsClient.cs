using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.Observability;

/// <summary>
/// Minimal session-aware client for the server-owned MCP operator contract. It deliberately owns
/// only transport concerns; the observe/diagnose/propose policy stays in
/// <see cref="OpsObserveDiagnoseProposeLoop"/>.
/// </summary>
internal sealed class HonuaMcpOpsClient(
    HttpClient httpClient,
    Uri endpoint,
    Action<HttpRequestMessage> applyAuthentication) : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const string SessionHeader = "Mcp-Session-Id";
    private const int MaxResponseBytes = 1024 * 1024;

    private long _nextRequestId;
    private string? _sessionId;
    private bool _disposed;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sessionId is not null)
        {
            return;
        }

        McpResponse response = await SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "honua-devops", version = "1" }
            },
            includeSession: false,
            cancellationToken).ConfigureAwait(false);

        if (!response.Root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("protocolVersion", out JsonElement negotiated) ||
            string.IsNullOrWhiteSpace(negotiated.GetString()))
        {
            throw new HonuaMcpContractException("initialize", "The server returned no negotiated MCP protocol version.");
        }

        if (string.IsNullOrWhiteSpace(response.SessionId))
        {
            throw new HonuaMcpContractException("initialize", "The server returned no MCP session id.");
        }

        _sessionId = response.SessionId;
        await SendNotificationAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
    }

    internal async Task<JsonElement> CallToolAsync(
        string toolName,
        object arguments,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sessionId is null)
        {
            throw new InvalidOperationException("The MCP session must be initialized before calling a tool.");
        }

        McpResponse response = await SendRequestAsync(
            "tools/call",
            new
            {
                name = toolName,
                arguments
            },
            includeSession: true,
            cancellationToken).ConfigureAwait(false);

        if (response.Root.TryGetProperty("error", out JsonElement rpcError))
        {
            throw new HonuaMcpContractException(toolName, ReadErrorMessage(rpcError, "JSON-RPC error"));
        }

        if (!response.Root.TryGetProperty("result", out JsonElement result))
        {
            throw new HonuaMcpContractException(toolName, "The MCP response omitted result.");
        }

        bool isError = result.TryGetProperty("isError", out JsonElement isErrorElement) &&
            isErrorElement.ValueKind == JsonValueKind.True;
        if (isError)
        {
            string detail = result.TryGetProperty("structuredContent", out JsonElement errorContent)
                ? ReadErrorMessage(errorContent, "MCP tool returned an error")
                : "MCP tool returned an error";
            throw new HonuaMcpToolException(toolName, detail);
        }

        if (!result.TryGetProperty("structuredContent", out JsonElement structuredContent) ||
            structuredContent.ValueKind != JsonValueKind.Object)
        {
            throw new HonuaMcpContractException(toolName, "The MCP tool response omitted structuredContent.");
        }

        return structuredContent.Clone();
    }

    private async Task<McpResponse> SendRequestAsync(
        string method,
        object parameters,
        bool includeSession,
        CancellationToken cancellationToken)
    {
        long requestId = Interlocked.Increment(ref _nextRequestId);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyHeaders(request, includeSession);

        using HttpResponseMessage response = await SendAsync(method, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HonuaMcpContractException(
                method,
                $"MCP endpoint returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        string? sessionId = response.Headers.TryGetValues(SessionHeader, out IEnumerable<string>? values)
            ? values.SingleOrDefault()
            : null;
        JsonElement root = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        ValidateResponseEnvelope(root, requestId, method);
        return new McpResponse(root, sessionId);
    }

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                method
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyHeaders(request, includeSession: true);

        using HttpResponseMessage response = await SendAsync(method, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HonuaMcpContractException(
                method,
                $"MCP notification returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private void ApplyHeaders(HttpRequestMessage request, bool includeSession)
    {
        applyAuthentication(request);
        if (includeSession && _sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation(SessionHeader, _sessionId);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string operation,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HonuaMcpContractException(operation, "The MCP request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new HonuaMcpContractException(operation, "The MCP endpoint could not be reached.", exception);
        }
    }

    private static async Task<JsonElement> ReadBoundedJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new HonuaMcpContractException("transport", "MCP response exceeded the 1 MiB safety limit.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new HonuaMcpContractException("transport", "MCP response exceeded the 1 MiB safety limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new HonuaMcpContractException("transport", "MCP endpoint returned invalid JSON.", exception);
        }
    }

    private static string ReadErrorMessage(JsonElement element, string fallback)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("message", out JsonElement message) &&
            message.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(message.GetString()))
        {
            return Redaction.Scrub(message.GetString()!);
        }

        return fallback;
    }

    private static void ValidateResponseEnvelope(JsonElement root, long expectedId, string method)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out JsonElement jsonRpc) ||
            !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal) ||
            !root.TryGetProperty("id", out JsonElement id) ||
            id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out long actualId) ||
            actualId != expectedId)
        {
            throw new HonuaMcpContractException(method, "The MCP response envelope or request id was invalid.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sessionId is null)
        {
            return;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Delete, endpoint);
            ApplyHeaders(request, includeSession: true);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Session cleanup is best effort and must not mask the operation result.
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout also surfaces as cancellation. Cleanup remains best effort.
        }

        _sessionId = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record McpResponse(JsonElement Root, string? SessionId);
}

internal class HonuaMcpContractException : Exception
{
    internal HonuaMcpContractException(string operation, string message, Exception? innerException = null)
        : base($"{operation}: {message}", innerException)
    {
        Operation = operation;
    }

    internal string Operation { get; }
}

internal sealed class HonuaMcpToolException : HonuaMcpContractException
{
    internal HonuaMcpToolException(string toolName, string message)
        : base(toolName, message)
    {
    }
}
