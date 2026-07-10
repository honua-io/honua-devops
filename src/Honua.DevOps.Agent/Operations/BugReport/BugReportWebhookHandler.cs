using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.Audit;

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
///   422 (permanent, non-retryable), no filing.</item>
///   <item><b>Idempotency</b> — an eventId already filed short-circuits to 409.</item>
/// </list>
/// Only then is the accept-side filing invoked (duplicate-issue detection +
/// sanitized filing), bounded by a per-request deadline. The eventId is consumed
/// ONLY after the filing confirms a terminal outcome; a transient
/// search/file/deadline failure leaves the id unclaimed and returns a non-2xx so
/// the signed sender retries — the repo-side duplicate search keeps that retry
/// from ever filing twice.
/// </summary>
internal sealed class BugReportWebhookHandler
{
    // See WorkIntakeWebhookHandler for the rationale: bound the outbound write so a
    // slow backend cannot hold the inbound delivery open indefinitely. When the
    // write outlives the deadline we cannot confirm it landed, so we ask the sender
    // to retry rather than consume the id on an unconfirmed file.
    internal static readonly TimeSpan DefaultAcceptedDeadline = TimeSpan.FromSeconds(10);

    private const string AuditToolName = "bug_report.webhook";

    private readonly byte[] _secret;
    private readonly ComponentRepoAllowlist _allowlist;
    private readonly TimeSpan _replayWindow;
    private readonly IEventIdempotencyStore _idempotencyStore;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<BugReport, RepoRef, CancellationToken, Task<BugReportFilingOutcome>>? _onAccepted;
    private readonly TimeSpan _acceptedDeadline;
    private readonly AuditContext? _auditContext;

    internal BugReportWebhookHandler(
        string secret,
        ComponentRepoAllowlist allowlist,
        TimeSpan replayWindow,
        Func<BugReport, RepoRef, CancellationToken, Task<BugReportFilingOutcome>>? onAccepted = null,
        IEventIdempotencyStore? idempotencyStore = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? acceptedDeadline = null,
        AuditContext? auditContext = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _replayWindow = replayWindow > TimeSpan.Zero ? replayWindow : TimeSpan.FromSeconds(BugReportConfiguration.DefaultReplayWindowSeconds);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _onAccepted = onAccepted;
        // Bound the default store to the replay window: an event older than that is
        // rejected on freshness grounds, so an entry never needs to outlive it.
        _idempotencyStore = idempotencyStore ?? new InMemoryEventIdempotencyStore(_replayWindow, _now);
        _acceptedDeadline = acceptedDeadline is { } deadline && deadline > TimeSpan.Zero
            ? deadline
            : DefaultAcceptedDeadline;
        _auditContext = auditContext;
    }

    internal async Task<BugReportHandlerResult> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        // 1. Signature — over the raw bytes, constant-time. Unsigned/invalid rejected.
        if (!WebhookSignatureVerifier.TryVerify(_secret, body, signatureHeader))
        {
            return await RejectAsync(
                401, "invalid-signature", "Rejected bug-report webhook: signature missing or invalid.",
                report: null, repo: null, cancellationToken);
        }

        // 2. Shape.
        BugReportWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BugReportWebhookPayload>(body, BugReportWebhookPayload.JsonOptions);
        }
        catch (JsonException)
        {
            return await RejectAsync(400, "malformed-json", "Rejected bug-report webhook: body is not valid JSON.", null, null, cancellationToken);
        }

        if (payload is null)
        {
            return await RejectAsync(400, "empty-payload", "Rejected bug-report webhook: empty payload.", null, null, cancellationToken);
        }

        if (!string.Equals(payload.EventType, BugReportWebhookPayload.ExpectedEvent, StringComparison.Ordinal))
        {
            return await RejectAsync(400, $"unexpected-event:{payload.EventType ?? "(none)"}", "Rejected bug-report webhook: unexpected event type.", null, null, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            return await RejectAsync(400, "missing-event-id", "Rejected bug-report webhook: missing eventId.", null, null, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(payload.TicketId))
        {
            return await RejectAsync(400, "missing-ticket-id", "Rejected bug-report webhook: missing ticketId.", null, null, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(payload.Component))
        {
            return await RejectAsync(400, "missing-component", "Rejected bug-report webhook: missing component.", null, null, cancellationToken);
        }

        if (payload.EmittedAt is not { } emittedAt)
        {
            return await RejectAsync(400, "missing-emitted-at", "Rejected bug-report webhook: missing emittedAt.", null, null, cancellationToken);
        }

        BugReport report = Normalize(payload, emittedAt);

        // 3. Freshness / replay window — reject events too old OR too far in the
        // future (clock skew). This bounds the replay surface for a captured event.
        TimeSpan age = _now() - report.EmittedAt;
        if (age > _replayWindow || age < -_replayWindow)
        {
            return await RejectAsync(400, "stale-timestamp", "Rejected bug-report: emittedAt is outside the replay window.", report, null, cancellationToken);
        }

        // 4. Allowlist — the destination repo comes ONLY from server-owned config.
        //    The event's component is just a lookup key. An unmapped component is a
        //    PERMANENT refusal (422, non-retryable) that consumes no id.
        if (!_allowlist.TryResolve(report.Component, out RepoRef repo))
        {
            return await RejectAsync(
                422, $"unmapped-component:{report.Component}",
                $"Refused bug-report: component `{report.Component}` is not mapped in the server-owned allowlist.",
                report, repo: null, cancellationToken);
        }

        // 5. Idempotency fast path — an eventId already filed is a no-op duplicate.
        if (_idempotencyStore.IsProcessed(report.EventId))
        {
            return await RejectAsync(409, "duplicate-event", "Skipped bug-report: eventId already filed.", report, repo, cancellationToken);
        }

        // 6. Accept-side filing, bounded by a per-request deadline.
        BugReportFilingOutcome outcome = BugReportFilingOutcome.ReportOnly;
        if (_onAccepted is not null)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_acceptedDeadline);
            try
            {
                outcome = await _onAccepted(report, repo, deadline.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The filing outlived its per-request deadline: we cannot confirm a
                // durable file, so DO NOT consume the id. Signal a retry; the
                // repo-side duplicate search makes it a no-op if the write landed.
                return await RejectAsync(503, "filing-timeout", "Bug-report filing exceeded its deadline; requesting sender retry.", report, repo, cancellationToken);
            }
        }

        // 7. Consume the eventId only on a confirmed terminal outcome; a transient
        //    failure leaves it unclaimed so the signed sender retries.
        return outcome switch
        {
            BugReportFilingOutcome.Filed => await AcceptAsync(
                report, repo, "accepted", "Filed sanitized bug-report issue.", mutated: true, cancellationToken),
            BugReportFilingOutcome.DuplicateSkipped => await AcceptAsync(
                report, repo, "duplicate-skip", "Bug already tracked by an open issue; no duplicate filed.", mutated: false, cancellationToken),
            BugReportFilingOutcome.ReportOnly => await AcceptAsync(
                report, repo, "accepted-report-only", "Issue filing disabled; sanitized issue prepared, not filed.", mutated: false, cancellationToken),
            BugReportFilingOutcome.SearchFailed => await RejectAsync(
                503, "duplicate-check-failed", "Could not confirm absence of a duplicate; requesting sender retry.", report, repo, cancellationToken),
            _ => await RejectAsync(
                502, "filing-failed", "Bug-report issue filing failed; requesting sender retry.", report, repo, cancellationToken),
        };
    }

    private async Task<BugReportHandlerResult> AcceptAsync(
        BugReport report,
        RepoRef repo,
        string reason,
        string summary,
        bool mutated,
        CancellationToken cancellationToken)
    {
        // The event is terminally handled — claim the id so a redelivery is a fast
        // no-op. A concurrent in-flight delivery is caught by the repo-side search.
        _idempotencyStore.TryMarkProcessed(report.EventId);
        await AuditAsync(reason, summary, report, repo, mutated, cancellationToken);
        return new BugReportHandlerResult(202, reason, report, repo);
    }

    private async Task<BugReportHandlerResult> RejectAsync(
        int statusCode,
        string reason,
        string summary,
        BugReport? report,
        RepoRef? repo,
        CancellationToken cancellationToken)
    {
        await AuditAsync(reason, summary, report, repo, mutated: false, cancellationToken);
        return new BugReportHandlerResult(statusCode, reason, report, repo);
    }

    private async Task AuditAsync(
        string status,
        string summary,
        BugReport? report,
        RepoRef? repo,
        bool mutated,
        CancellationToken cancellationToken)
    {
        if (_auditContext is null || _auditContext.Sink is NullAuditSink)
        {
            return;
        }

        Dictionary<string, string> arguments = new(StringComparer.Ordinal)
        {
            ["eventType"] = BugReportWebhookPayload.ExpectedEvent,
        };
        if (report is not null)
        {
            arguments["eventId"] = Redaction.Scrub(report.EventId);
            arguments["ticketId"] = Redaction.Scrub(report.TicketId);
            arguments["component"] = Redaction.Scrub(report.Component);
        }
        if (repo is not null)
        {
            arguments["repo"] = repo.FullName;
        }

        AuditRecord record = new(
            Timestamp: _now(),
            SessionId: _auditContext.SessionId,
            OperationId: Guid.NewGuid().ToString("n"),
            ToolName: AuditToolName,
            Arguments: arguments,
            Status: status,
            Summary: Redaction.Scrub(summary),
            Mutated: mutated,
            ExecutionMode: _auditContext.ExecutionMode,
            ExecutionTier: _auditContext.ExecutionTier,
            ApprovalMode: _auditContext.ApprovalMode,
            Provider: _auditContext.Provider,
            BackendSteps: null,
            Evidence: null);

        try
        {
            await _auditContext.Sink.WriteAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warn: bug-report audit write failed: {exception.Message}");
        }
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
}

internal sealed record BugReportHandlerResult(
    int StatusCode,
    string Reason,
    BugReport? Report,
    RepoRef? Repo);
