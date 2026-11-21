using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Operations;

internal sealed class EscalationWebhookHandler
{
    private static readonly TimeSpan DefaultIdempotencyRetention = TimeSpan.FromHours(1);

    private readonly byte[] _secret;
    private readonly Func<EscalationWebhookPayload, CancellationToken, Task>? _onAccepted;
    private readonly TimeSpan? _replayWindow;
    private readonly IEventIdempotencyStore _idempotencyStore;
    private readonly Func<DateTimeOffset> _now;

    internal EscalationWebhookHandler(
        string secret,
        Func<EscalationWebhookPayload, CancellationToken, Task>? onAccepted = null,
        TimeSpan? replayWindow = null,
        IEventIdempotencyStore? idempotencyStore = null,
        Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _onAccepted = onAccepted;
        _replayWindow = replayWindow is { } window && window > TimeSpan.Zero ? window : null;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        TimeSpan retention = _replayWindow ?? DefaultIdempotencyRetention;
        _idempotencyStore = idempotencyStore ?? new InMemoryEventIdempotencyStore(retention, _now);
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

        if (!string.Equals(payload.EventType, EscalationWebhookPayload.ExpectedEvent, StringComparison.Ordinal))
        {
            return new WebhookHandlerResult(
                StatusCode: 400,
                Reason: $"unexpected-event:{payload.EventType}",
                Payload: null);
        }

        if (_replayWindow is { } replayWindow)
        {
            if (payload.EscalatedAt is not { } escalatedAt)
            {
                return new WebhookHandlerResult(
                    StatusCode: 400,
                    Reason: "missing-escalated-at",
                    Payload: payload);
            }

            TimeSpan age = _now() - escalatedAt;
            if (age > replayWindow || age < -replayWindow)
            {
                return new WebhookHandlerResult(
                    StatusCode: 400,
                    Reason: "stale-timestamp",
                    Payload: payload);
            }
        }

        string? normalizedEventId = string.IsNullOrWhiteSpace(payload.EventId)
            ? null
            : payload.EventId.Trim();

        if (normalizedEventId is not null && _idempotencyStore.IsProcessed(normalizedEventId))
        {
            return new WebhookHandlerResult(
                StatusCode: 409,
                Reason: "duplicate-event",
                Payload: payload);
        }

        if (_onAccepted is not null)
        {
            await _onAccepted(payload, cancellationToken);
        }

        if (normalizedEventId is not null)
        {
            _idempotencyStore.TryMarkProcessed(normalizedEventId);
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
