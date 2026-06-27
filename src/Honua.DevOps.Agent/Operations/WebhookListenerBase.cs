using System.Net;
using System.Text;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Shared HTTP transport for honua-devops webhook listeners. Owns the BCL-only
/// <see cref="HttpListener"/> accept loop, POST/path validation, a bounded body
/// read (size cap + read deadline) performed <em>before</em> authentication, the
/// JSON response writer, and lifecycle. Concrete listeners only supply the
/// signature-header name, a startup banner, and the authenticated handler.
///
/// Centralizing the transport means request-handling fixes (e.g. the body-size
/// cap below) live in exactly one place instead of drifting between the
/// escalation and work-intake siblings.
/// </summary>
internal abstract class WebhookListenerBase : IAsyncDisposable
{
    /// <summary>Default inbound request body cap (1 MiB).</summary>
    internal const long DefaultMaxBodyBytes = 1L * 1024 * 1024;

    /// <summary>Default deadline for reading the full request body.</summary>
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);

    private readonly int _port;
    private readonly string _path;
    private readonly long _maxBodyBytes;
    private readonly TimeSpan _readTimeout;
    private readonly HttpListener _httpListener;

    protected TextWriter Stdout { get; }
    protected TextWriter Stderr { get; }

    /// <summary>Header carrying the HMAC signature for this listener.</summary>
    protected abstract string SignatureHeaderName { get; }

    /// <summary>Short label used in rejection / I/O log lines (e.g. "webhook").</summary>
    protected abstract string ListenerLabel { get; }

    protected WebhookListenerBase(
        int port,
        string path,
        TextWriter? stdout,
        TextWriter? stderr,
        long maxBodyBytes = DefaultMaxBodyBytes,
        TimeSpan? readTimeout = null)
    {
        _port = port;
        _path = path;
        _maxBodyBytes = maxBodyBytes > 0 ? maxBodyBytes : DefaultMaxBodyBytes;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        Stdout = stdout ?? Console.Out;
        Stderr = stderr ?? Console.Error;
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://localhost:{port}{NormalizePrefix(path)}");
    }

    internal Uri EndpointUri => new($"http://localhost:{_port}{_path}");

    /// <summary>
    /// Authenticate and process a fully-buffered request body. The body has
    /// already been size-capped before this is called.
    /// </summary>
    protected abstract Task<(int StatusCode, string Reason)> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken);

    /// <summary>Emit the two-line startup banner once the listener is bound.</summary>
    protected abstract void WriteStartupBanner(Uri endpoint);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        _httpListener.Start();
        WriteStartupBanner(EndpointUri);

        using CancellationTokenRegistration shutdown = cancellationToken.Register(() =>
        {
            try
            {
                _httpListener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // already stopped
            }
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _httpListener.GetContextAsync();
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(context, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            if (_httpListener.IsListening)
            {
                _httpListener.Stop();
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync(context, 405, "method-not-allowed", cancellationToken);
                return;
            }

            if (!string.Equals(context.Request.Url?.AbsolutePath, _path, StringComparison.Ordinal))
            {
                await RespondAsync(context, 404, "not-found", cancellationToken);
                return;
            }

            // Reject oversized payloads on the declared length before buffering a
            // single byte, so a hostile Content-Length cannot force an allocation.
            if (context.Request.ContentLength64 > _maxBodyBytes)
            {
                await RespondAsync(context, 413, "payload-too-large", cancellationToken);
                return;
            }

            byte[] body;
            using (CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readCts.CancelAfter(_readTimeout);
                try
                {
                    body = await ReadCappedBodyAsync(context.Request, readCts.Token);
                }
                catch (BodyTooLargeException)
                {
                    // Chunked/streamed body that exceeded the cap mid-flight.
                    await RespondAsync(context, 413, "payload-too-large", cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                    when (readCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Slow/stalled upload hit the read deadline.
                    await RespondAsync(context, 408, "request-timeout", cancellationToken);
                    return;
                }
            }

            string? signatureHeader = context.Request.Headers[SignatureHeaderName];
            (int statusCode, string reason) = await HandleAsync(body, signatureHeader, cancellationToken);

            if (statusCode is < 200 or >= 300)
            {
                Stderr.WriteLine($"{ListenerLabel} rejected: status={statusCode} reason={reason}");
            }

            await RespondAsync(context, statusCode, reason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
        {
            Stderr.WriteLine($"{ListenerLabel} listener I/O error: {exception.Message}");
        }
    }

    /// <summary>
    /// Copy the request body into memory, aborting with
    /// <see cref="BodyTooLargeException"/> the moment the running total exceeds
    /// the cap. This bounds memory for chunked requests where
    /// <c>ContentLength64</c> is unknown (-1) up front.
    /// </summary>
    private async Task<byte[]> ReadCappedBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        long declared = request.ContentLength64;
        int initialCapacity = declared is > 0 and <= int.MaxValue ? (int)declared : 0;
        using MemoryStream buffer = new(initialCapacity);

        byte[] chunk = new byte[81920];
        Stream input = request.InputStream;
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > _maxBodyBytes)
            {
                throw new BodyTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private static async Task RespondAsync(
        HttpListenerContext context,
        int statusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            byte[] body = Encoding.UTF8.GetBytes($"{{\"status\":{statusCode},\"reason\":{EncodeJsonString(reason)}}}");
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
        {
            // client may have hung up; nothing to do
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException)
            {
                // already closed
            }
        }
    }

    /// <summary>
    /// Minimal RFC 8259 JSON string encoder so a reason containing a quote,
    /// backslash, or control character cannot break the response envelope.
    /// </summary>
    private static string EncodeJsonString(string value)
    {
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string NormalizePrefix(string path)
    {
        // HttpListener prefixes must end with `/` to act as a path prefix; we match
        // the exact path inside HandleContextAsync.
        return path.EndsWith('/') ? path : path + "/";
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_httpListener.IsListening)
            {
                _httpListener.Stop();
            }

            _httpListener.Close();
        }
        catch (ObjectDisposedException)
        {
            // already closed
        }

        return ValueTask.CompletedTask;
    }

    private sealed class BodyTooLargeException : Exception;
}
