using System.Net;
using System.Text;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Lightweight HTTP listener for the escalation webhook. Uses BCL-only
/// <see cref="HttpListener"/> so honua-devops stays on the plain
/// Microsoft.NET.Sdk console SDK with zero added dependencies.
/// </summary>
internal sealed class EscalationWebhookListener : IAsyncDisposable
{
    private readonly WebhookListenerConfiguration _configuration;
    private readonly EscalationWebhookHandler _handler;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly HttpListener _httpListener;

    internal EscalationWebhookListener(
        WebhookListenerConfiguration configuration,
        EscalationWebhookHandler handler,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        _configuration = configuration;
        _handler = handler;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://localhost:{configuration.Port}{NormalizePrefix(configuration.Path)}");
    }

    internal Uri EndpointUri => new($"http://localhost:{_configuration.Port}{_configuration.Path}");

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        _httpListener.Start();
        _stdout.WriteLine($"honua-devops listener bound to {EndpointUri} (auto-triage={_configuration.AutoTriage.ToString().ToLowerInvariant()}).");
        _stdout.WriteLine("Awaiting signed escalation webhooks. Press Ctrl+C to exit.");

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

            if (!string.Equals(context.Request.Url?.AbsolutePath, _configuration.Path, StringComparison.Ordinal))
            {
                await RespondAsync(context, 404, "not-found", cancellationToken);
                return;
            }

            byte[] body;
            using (MemoryStream buffer = new())
            {
                await context.Request.InputStream.CopyToAsync(buffer, cancellationToken);
                body = buffer.ToArray();
            }

            string? signatureHeader = context.Request.Headers[EscalationWebhookPayload.SignatureHeader];
            WebhookHandlerResult result = await _handler.HandleAsync(body, signatureHeader, cancellationToken);

            if (result.StatusCode is < 200 or >= 300)
            {
                _stderr.WriteLine($"webhook rejected: status={result.StatusCode} reason={result.Reason}");
            }

            await RespondAsync(context, result.StatusCode, result.Reason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
        {
            _stderr.WriteLine($"webhook listener I/O error: {exception.Message}");
        }
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
            byte[] body = Encoding.UTF8.GetBytes($"{{\"status\":{statusCode},\"reason\":\"{reason}\"}}");
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
}
