using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class BugReportWebhookHandlerTests
{
    private const string Secret = "bugreport-test-secret";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

    private static ComponentRepoAllowlist Allowlist()
        => ComponentRepoAllowlist.Parse("sdk-js=honua-io/honua-sdk-js,server=honua-io/honua-server");

    private static Func<BugReport, RepoRef, CancellationToken, Task<BugReportFilingOutcome>> Files(Action? onCall = null)
        => (_, _, _) =>
        {
            onCall?.Invoke();
            return Task.FromResult(BugReportFilingOutcome.Filed);
        };

    private static BugReportWebhookHandler NewHandler(
        Func<BugReport, RepoRef, CancellationToken, Task<BugReportFilingOutcome>>? onAccepted = null,
        IEventIdempotencyStore? store = null,
        TimeSpan? window = null,
        TimeSpan? deadline = null,
        AuditContext? audit = null)
        => new(
            Secret,
            Allowlist(),
            window ?? TimeSpan.FromMinutes(5),
            onAccepted,
            idempotencyStore: store,
            now: () => FixedNow,
            acceptedDeadline: deadline,
            auditContext: audit);

    private static AuditContext AuditWith(RecordingAuditSink sink)
        => new("session-test", "plan", "standard", "pr-first", "codex", sink);

    private static (byte[] Body, string Signature) SignedBody(object payload)
    {
        string json = JsonSerializer.Serialize(payload, BugReportWebhookPayload.JsonOptions);
        byte[] body = Encoding.UTF8.GetBytes(json);
        return (body, WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json));
    }

    private static object ValidPayload(
        string eventId = "evt-bug-1",
        string component = "sdk-js",
        string? ticketId = "ST-2026-0001",
        DateTimeOffset? emittedAt = null)
        => new
        {
            eventId,
            eventType = "ticket.bug_report.v1",
            emittedAt = emittedAt ?? FixedNow,
            ticketId,
            component,
            severity = "high",
            environment = "prod",
            service = "tiles-api",
            title = "Tiles fail to render at zoom 18",
            summary = "Repro captured in fixture; see references.",
            fingerprint = "fp-abc123",
            envelopeRefs = new[] { "env-ref-1", "env-ref-2" },
            fixtureRefs = new[] { "fx-ref-9" },
            ticketUrl = "https://support.example.test/tickets/ST-2026-0001"
        };

    [Fact]
    public async Task Rejects_UnsignedRequest()
    {
        bool invoked = false;
        BugReportWebhookHandler handler = NewHandler(Files(() => invoked = true));
        (byte[] body, _) = SignedBody(ValidPayload());

        BugReportHandlerResult result = await handler.HandleAsync(body, signatureHeader: null, CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("invalid-signature", result.Reason);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Rejects_TamperedSignature()
    {
        BugReportWebhookHandler handler = NewHandler();
        (byte[] body, _) = SignedBody(ValidPayload());

        BugReportHandlerResult result = await handler.HandleAsync(body, "sha256=" + new string('0', 64), CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("invalid-signature", result.Reason);
    }

    [Fact]
    public async Task Rejects_MalformedJson()
    {
        BugReportWebhookHandler handler = NewHandler();
        const string json = "{not json";
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("malformed-json", result.Reason);
    }

    [Fact]
    public async Task Rejects_WrongEventType()
    {
        BugReportWebhookHandler handler = NewHandler();
        (byte[] body, string signature) = SignedBody(new
        {
            eventId = "evt-1",
            eventType = "ticket.escalation_requested",
            emittedAt = FixedNow,
            ticketId = "ST-1",
            component = "sdk-js"
        });

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.StartsWith("unexpected-event", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-event-id", null, "sdk-js", "ST-1")]
    [InlineData("missing-ticket-id", "evt-1", "sdk-js", null)]
    [InlineData("missing-component", "evt-1", null, "ST-1")]
    public async Task Rejects_MissingRequiredFields(string expectedReason, string? eventId, string? component, string? ticketId)
    {
        BugReportWebhookHandler handler = NewHandler();
        (byte[] body, string signature) = SignedBody(new
        {
            eventId,
            eventType = "ticket.bug_report.v1",
            emittedAt = FixedNow,
            ticketId,
            component
        });

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public async Task Rejects_MissingEmittedAt()
    {
        BugReportWebhookHandler handler = NewHandler();
        (byte[] body, string signature) = SignedBody(new
        {
            eventId = "evt-1",
            eventType = "ticket.bug_report.v1",
            ticketId = "ST-1",
            component = "sdk-js"
        });

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("missing-emitted-at", result.Reason);
    }

    [Fact]
    public async Task Rejects_StaleTimestamp_TooOld()
    {
        bool invoked = false;
        BugReportWebhookHandler handler = NewHandler(Files(() => invoked = true));
        (byte[] body, string signature) = SignedBody(ValidPayload(emittedAt: FixedNow.AddMinutes(-10)));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("stale-timestamp", result.Reason);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Rejects_StaleTimestamp_TooFarInFuture()
    {
        BugReportWebhookHandler handler = NewHandler();
        (byte[] body, string signature) = SignedBody(ValidPayload(emittedAt: FixedNow.AddMinutes(10)));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("stale-timestamp", result.Reason);
    }

    [Fact]
    public async Task Rejects_UnmappedComponent_NoFiling()
    {
        bool invoked = false;
        BugReportWebhookHandler handler = NewHandler(Files(() => invoked = true));
        (byte[] body, string signature) = SignedBody(ValidPayload(component: "some-unknown-component"));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(422, result.StatusCode);
        Assert.StartsWith("unmapped-component", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Repo);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Accepts_ResolvesRepoFromAllowlistOnly()
    {
        BugReport? capturedReport = null;
        RepoRef? capturedRepo = null;
        BugReportWebhookHandler handler = NewHandler((report, repo, _) =>
        {
            capturedReport = report;
            capturedRepo = repo;
            return Task.FromResult(BugReportFilingOutcome.Filed);
        });

        // The event names component `server`; the destination must come from the
        // allowlist entry (honua-io/honua-server), never from any event field.
        (byte[] body, string signature) = SignedBody(ValidPayload(component: "server"));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("accepted", result.Reason);
        Assert.NotNull(capturedRepo);
        Assert.Equal("honua-io/honua-server", capturedRepo!.FullName);
        Assert.NotNull(capturedReport);
        Assert.Equal("ST-2026-0001", capturedReport!.TicketId);
        Assert.Equal("fp-abc123", capturedReport.Fingerprint);
        Assert.Equal(2, capturedReport.EnvelopeRefs.Count);
    }

    [Fact]
    public async Task DuplicateEventId_FilesOnce()
    {
        int fileCount = 0;
        IEventIdempotencyStore store = new InMemoryEventIdempotencyStore();
        BugReportWebhookHandler handler = NewHandler(
            Files(() => Interlocked.Increment(ref fileCount)),
            store: store);

        (byte[] body, string signature) = SignedBody(ValidPayload(eventId: "evt-dup"));

        BugReportHandlerResult first = await handler.HandleAsync(body, signature, CancellationToken.None);
        BugReportHandlerResult second = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, first.StatusCode);
        Assert.Equal("accepted", first.Reason);
        Assert.Equal(409, second.StatusCode);
        Assert.Equal("duplicate-event", second.Reason);
        Assert.Equal(1, fileCount);
    }

    [Fact]
    public async Task UnmappedComponent_DoesNotConsumeEventId()
    {
        // An unmapped component is refused before the idempotency claim, so a later
        // corrected send (same eventId, now mapped) is not swallowed as a duplicate.
        int fileCount = 0;
        IEventIdempotencyStore store = new InMemoryEventIdempotencyStore();

        BugReportWebhookHandler unmapped = NewHandler(
            Files(() => Interlocked.Increment(ref fileCount)), store: store);
        (byte[] badBody, string badSig) = SignedBody(ValidPayload(eventId: "evt-x", component: "nope"));
        BugReportHandlerResult refused = await unmapped.HandleAsync(badBody, badSig, CancellationToken.None);
        Assert.Equal(422, refused.StatusCode);

        BugReportWebhookHandler mapped = NewHandler(
            Files(() => Interlocked.Increment(ref fileCount)), store: store);
        (byte[] goodBody, string goodSig) = SignedBody(ValidPayload(eventId: "evt-x", component: "sdk-js"));
        BugReportHandlerResult accepted = await mapped.HandleAsync(goodBody, goodSig, CancellationToken.None);

        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal(1, fileCount);
    }

    [Fact]
    public async Task SlowFiling_ExceedsDeadline_DoesNotConsumeId_RequestsRetry()
    {
        // FIX 3: an unconfirmed (deadline-exceeded) filing must not consume the id;
        // the handler asks the sender to retry with a non-2xx.
        InMemoryEventIdempotencyStore store = new();
        BugReportWebhookHandler handler = NewHandler(
            onAccepted: async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return BugReportFilingOutcome.Filed;
            },
            store: store,
            deadline: TimeSpan.FromMilliseconds(50));
        (byte[] body, string signature) = SignedBody(ValidPayload(eventId: "evt-slow"));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("filing-timeout", result.Reason);
        Assert.False(store.IsProcessed("evt-slow"));
    }

    [Fact]
    public async Task TransientFilingFailure_DoesNotConsumeId_ReturnsNon2xx_ThenRetrySucceeds()
    {
        // FIX 3: a transient file failure leaves the id unclaimed and returns a
        // non-2xx; a subsequent retry (same eventId) then files and consumes it.
        InMemoryEventIdempotencyStore store = new();
        int attempts = 0;
        BugReportWebhookHandler handler = NewHandler(
            onAccepted: (_, _, _) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? BugReportFilingOutcome.FilingFailed
                    : BugReportFilingOutcome.Filed);
            },
            store: store);
        (byte[] body, string signature) = SignedBody(ValidPayload(eventId: "evt-transient"));

        BugReportHandlerResult first = await handler.HandleAsync(body, signature, CancellationToken.None);
        Assert.Equal(502, first.StatusCode);
        Assert.Equal("filing-failed", first.Reason);
        Assert.False(store.IsProcessed("evt-transient"));

        BugReportHandlerResult second = await handler.HandleAsync(body, signature, CancellationToken.None);
        Assert.Equal(202, second.StatusCode);
        Assert.Equal("accepted", second.Reason);
        Assert.True(store.IsProcessed("evt-transient"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SearchFailure_DoesNotConsumeId_RequestsRetry()
    {
        InMemoryEventIdempotencyStore store = new();
        BugReportWebhookHandler handler = NewHandler(
            onAccepted: (_, _, _) => Task.FromResult(BugReportFilingOutcome.SearchFailed),
            store: store);
        (byte[] body, string signature) = SignedBody(ValidPayload(eventId: "evt-search"));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("duplicate-check-failed", result.Reason);
        Assert.False(store.IsProcessed("evt-search"));
    }

    [Fact]
    public async Task DuplicateSkipped_ConsumesId_Acks()
    {
        InMemoryEventIdempotencyStore store = new();
        BugReportWebhookHandler handler = NewHandler(
            onAccepted: (_, _, _) => Task.FromResult(BugReportFilingOutcome.DuplicateSkipped),
            store: store);
        (byte[] body, string signature) = SignedBody(ValidPayload(eventId: "evt-dupskip"));

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("duplicate-skip", result.Reason);
        Assert.True(store.IsProcessed("evt-dupskip"));
    }

    [Fact]
    public async Task Audit_RecordsInvalidSignature_Unmapped_And_FilingFailure()
    {
        // FIX 4: rejected/unmapped/failed outcomes must produce a durable audit
        // record, not just stderr.
        RecordingAuditSink sink = new();

        BugReportWebhookHandler signatureHandler = NewHandler(audit: AuditWith(sink));
        (byte[] body, _) = SignedBody(ValidPayload(eventId: "evt-audit-sig"));
        await signatureHandler.HandleAsync(body, signatureHeader: null, CancellationToken.None);

        BugReportWebhookHandler unmappedHandler = NewHandler(Files(), audit: AuditWith(sink));
        (byte[] unmappedBody, string unmappedSig) = SignedBody(ValidPayload(eventId: "evt-audit-unmapped", component: "nope"));
        await unmappedHandler.HandleAsync(unmappedBody, unmappedSig, CancellationToken.None);

        BugReportWebhookHandler failHandler = NewHandler(
            onAccepted: (_, _, _) => Task.FromResult(BugReportFilingOutcome.FilingFailed),
            audit: AuditWith(sink));
        (byte[] failBody, string failSig) = SignedBody(ValidPayload(eventId: "evt-audit-fail"));
        await failHandler.HandleAsync(failBody, failSig, CancellationToken.None);

        Assert.Contains(sink.Records, r => r.Status == "invalid-signature");
        Assert.Contains(sink.Records, r => r.Status.StartsWith("unmapped-component", StringComparison.Ordinal));
        Assert.Contains(sink.Records, r => r.Status == "filing-failed");
        // The unmapped record carries the component for triage.
        AuditRecord unmapped = sink.Records.First(r => r.Status.StartsWith("unmapped-component", StringComparison.Ordinal));
        Assert.Equal("nope", unmapped.Arguments["component"]);
    }

    [Fact]
    public async Task PropagatesListenerShutdownCancellation()
    {
        using CancellationTokenSource shutdown = new();
        BugReportWebhookHandler handler = NewHandler(
            onAccepted: async (_, _, token) =>
            {
                shutdown.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return BugReportFilingOutcome.Filed;
            },
            deadline: TimeSpan.FromSeconds(30));
        (byte[] body, string signature) = SignedBody(ValidPayload());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(body, signature, shutdown.Token));
    }

    private sealed class RecordingAuditSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = new();

        public string Target => "recording";

        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
