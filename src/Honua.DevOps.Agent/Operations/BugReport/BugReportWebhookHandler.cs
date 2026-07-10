using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Processes a single inbound <c>ticket.bug_report.v1</c> webhook. The HTTP
/// transport is kept separate so this is unit-testable from raw bytes + headers,
/// exactly like <see cref="EscalationWebhookHandler"/> and
/// <see cref="Honua.DevOps.Agent.Operations.WorkIntake.WorkIntakeWebhookHandler"/>.
///
/// Trust pipeline (each step short-circuits with an auditable non-2xx):
/// <list type="number">
///   <item><b>Signature</b> — HMAC-SHA256 over the raw body via the shared
///   <see cref="WebhookSignatureVerifier"/> recipe. Unsigned/invalid ⇒ 401.</item>
///   <item><b>Shape</b> — JSON parse + exact event-type match + required fields.</item>
///   <item><b>Freshness / replay</b> — <c>emittedAt</c> must be within the
///   bounded replay window (past or future). Stale ⇒ 400.</item>
///   <item><b>Allowlist</b> — the destination repo is resolved ONLY from the
///   server-owned <see cref="ComponentRepoAllowlist"/>. An unmapped component ⇒
///   422, no filing.</item>
///   <item><b>Idempotency</b> — a per-<c>eventId</c> claim guarantees the same
///   event never files twice; a duplicate ⇒ 409, no filing.</item>
/// </list>
/// Only after all five does it invoke <c>onAccepted</c> (duplicate-issue
/// detection + sanitized filing), bounded by a per-request deadline so a slow
/// GitHub write cannot hold the inbound delivery open past the sender's retry
/// window.
/// </summary>
internal sealed class BugReportWebhookHandler
{
    // See WorkIntakeWebhookHandler for the rationale: bound the outbound write so a
    // slow backend cannot hold the inbound delivery open long enough that the
    // sender retries. Per-eventId idempotency + issue dedupe already make a retry a
    // no-op, but returning promptly avoids the retry entirely.
    internal static readonly TimeSpan DefaultAcceptedDeadline = TimeSpan.FromSeconds(10);

    private readonly byte[] _secret;
    private readonly ComponentRepoAllowlist _allowlist;
    private readonly TimeSpan _replayWindow;
    private readonly IEventIdempotencyStore _idempotencyStore;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<BugReport, RepoRef, CancellationToken, Task>? _onAccepted;
    private readonly TimeSpan _acceptedDeadline;

    internal BugReportWebhookHandler(
        string secret,
        ComponentRepoAllowlist allowlist,
        TimeSpan replayWindow,
        Func<BugReport, RepoRef, CancellationToken, Task>? onAccepted = null,
        IEventIdempotencyStore? idempotencyStore = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? acceptedDeadline = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _replayWindow = replayWindow > TimeSpan.Zero ? replayWindow : TimeSpan.FromSeconds(BugReportConfiguration.DefaultReplayWindowSeconds);
        _onAccepted = onAccepted;
        _idempotencyStore = idempotencyStore ?? new InMemoryEventIdempotencyStore();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _acceptedDeadline = acceptedDeadline is { } deadline && deadline > TimeSpan.Zero
            ? deadline
            : DefaultAcceptedDeadline;
    }

    internal async Task<BugReportHandlerResult> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        // 1. Signature — over the raw bytes, constant-time. Unsigned/invalid rejected.
        if (!WebhookSignatureVerifier.TryVerify(_secret, body, signatureHeader))
        {
            return Reject(401, "invalid-signature");
        }

        // 2. Shape.
        BugReportWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BugReportWebhookPayload>(body, BugReportWebhookPayload.JsonOptions);
        }
        catch (JsonException)
        {
            return Reject(400, "malformed-json");
        }

        if (payload is null)
        {
            return Reject(400, "empty-payload");
        }

        if (!string.Equals(payload.EventType, BugReportWebhookPayload.ExpectedEvent, StringComparison.Ordinal))
        {
            return Reject(400, $"unexpected-event:{payload.EventType ?? "(none)"}");
        }

        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            return Reject(400, "missing-event-id");
        }

        if (string.IsNullOrWhiteSpace(payload.TicketId))
        {
            return Reject(400, "missing-ticket-id");
        }

        if (string.IsNullOrWhiteSpace(payload.Component))
        {
            return Reject(400, "missing-component");
        }

        if (payload.EmittedAt is not { } emittedAt)
        {
            return Reject(400, "missing-emitted-at");
        }

        // 3. Freshness / replay window — reject events too old OR too far in the
        // future (clock skew). This bounds the replay surface for a captured event.
        TimeSpan age = _now() - emittedAt;
        if (age > _replayWindow || age < -_replayWindow)
        {
            return Reject(400, "stale-timestamp");
        }

        BugReport report = Normalize(payload, emittedAt);

        // 4. Allowlist — the destination repo comes ONLY from server-owned config.
        //    The event's component is just a lookup key; an unmapped component is a
        //    surfaced, audited refusal with no filing.
        if (!_allowlist.TryResolve(report.Component, out RepoRef repo))
        {
            return new BugReportHandlerResult(422, $"unmapped-component:{report.Component}", report, Repo: null);
        }

        // 5. Idempotency — claim the eventId AFTER the allowlist resolves so an
        //    unmapped event does not consume the id, but BEFORE filing so the same
        //    event can never file two issues. The claim is atomic.
        if (!_idempotencyStore.TryMarkProcessed(report.EventId))
        {
            return new BugReportHandlerResult(409, "duplicate-event", report, repo);
        }

        if (_onAccepted is not null)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_acceptedDeadline);
            try
            {
                await _onAccepted(report, repo, deadline.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The filing outlived its per-request deadline. The event is still
                // accepted (idempotency already claimed the id; a sender retry is a
                // no-op) — ack now rather than risk a delivery-retry.
                return new BugReportHandlerResult(202, "accepted-filing-deadline", report, repo);
            }
        }

        return new BugReportHandlerResult(202, "accepted", report, repo);
    }

    private static BugReport Normalize(BugReportWebhookPayload payload, DateTimeOffset emittedAt)
        => new(
            EventId: payload.EventId!.Trim(),
            EmittedAt: emittedAt,
            TicketId: payload.TicketId!.Trim(),
            Component: payload.Component!.Trim(),
            Severity: Normalize(payload.Severity),
            Environment: Normalize(payload.Environment),
            Service: Normalize(payload.Service),
            Title: Normalize(payload.Title),
            Summary: Normalize(payload.Summary),
            Fingerprint: Normalize(payload.Fingerprint),
            EnvelopeRefs: NormalizeRefs(payload.EnvelopeRefs),
            FixtureRefs: NormalizeRefs(payload.FixtureRefs),
            TicketUrl: Normalize(payload.TicketUrl));

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeRefs(IReadOnlyList<string>? refs)
    {
        if (refs is null || refs.Count == 0)
        {
            return Array.Empty<string>();
        }

        return refs
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .ToArray();
    }

    private static BugReportHandlerResult Reject(int statusCode, string reason)
        => new(statusCode, reason, Report: null, Repo: null);
}

internal sealed record BugReportHandlerResult(
    int StatusCode,
    string Reason,
    BugReport? Report,
    RepoRef? Repo);
