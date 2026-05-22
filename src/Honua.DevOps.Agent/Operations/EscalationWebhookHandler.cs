using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Processes a single inbound escalation webhook request. The HTTP transport
/// (HttpListener) is kept separate so this can be unit-tested by passing in
/// the raw body and headers — no socket needed.
/// </summary>
internal sealed class EscalationWebhookHandler
{
    private readonly byte[] _secret;
    private readonly Func<EscalationWebhookPayload, CancellationToken, Task>? _onAccepted;

    internal EscalationWebhookHandler(
        string secret,
        Func<EscalationWebhookPayload, CancellationToken, Task>? onAccepted = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _onAccepted = onAccepted;
    }

    internal async Task<WebhookHandlerResult> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        if (!WebhookSignatureVerifier.TryVerify(_secret, body, signatureHeader))
        {
            return new WebhookHandlerResult(
                StatusCode: 401,
                Reason: "invalid-signature",
                Payload: null);
        }

        EscalationWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EscalationWebhookPayload>(
                body,
                EscalationWebhookPayload.JsonOptions);
        }
        catch (JsonException)
        {
            return new WebhookHandlerResult(
                StatusCode: 400,
                Reason: "malformed-json",
                Payload: null);
        }

        if (payload is null)
        {
            return new WebhookHandlerResult(
                StatusCode: 400,
                Reason: "empty-payload",
                Payload: null);
        }

        if (!string.Equals(payload.Event, EscalationWebhookPayload.ExpectedEvent, StringComparison.Ordinal))
        {
            return new WebhookHandlerResult(
                StatusCode: 400,
                Reason: $"unexpected-event:{payload.Event}",
                Payload: null);
        }

        if (_onAccepted is not null)
        {
            await _onAccepted(payload, cancellationToken);
        }

        return new WebhookHandlerResult(
            StatusCode: 202,
            Reason: "accepted",
            Payload: payload);
    }
}

internal sealed record WebhookHandlerResult(
    int StatusCode,
    string Reason,
    EscalationWebhookPayload? Payload);
