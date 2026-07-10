using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class BugReportWebhookHandlerTests
{
    private const string Secret = "bugreport-test-secret";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

    private static ComponentRepoAllowlist Allowlist()
        => ComponentRepoAllowlist.Parse("sdk-js=honua-io/honua-sdk-js,server=honua-io/honua-server");

    private static BugReportWebhookHandler NewHandler(
        Func<BugReport, RepoRef, CancellationToken, Task>? onAccepted = null,
        IEventIdempotencyStore? store = null,
        TimeSpan? window = null,
        TimeSpan? deadline = null)
        => new(
            Secret,
            Allowlist(),
            window ?? TimeSpan.FromMinutes(5),
            onAccepted,
            idempotencyStore: store,
            now: () => FixedNow,
            acceptedDeadline: deadline);

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
        BugReportWebhookHandler handler = NewHandler((_, _, _) => { invoked = true; return Task.CompletedTask; });
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
        BugReportWebhookHandler handler = NewHandler((_, _, _) => { invoked = true; return Task.CompletedTask; });
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
        BugReportWebhookHandler handler = NewHandler((_, _, _) => { invoked = true; return Task.CompletedTask; });
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
            return Task.CompletedTask;
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
            (_, _, _) => { Interlocked.Increment(ref fileCount); return Task.CompletedTask; },
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
            (_, _, _) => { Interlocked.Increment(ref fileCount); return Task.CompletedTask; }, store: store);
        (byte[] badBody, string badSig) = SignedBody(ValidPayload(eventId: "evt-x", component: "nope"));
        BugReportHandlerResult refused = await unmapped.HandleAsync(badBody, badSig, CancellationToken.None);
        Assert.Equal(422, refused.StatusCode);

        BugReportWebhookHandler mapped = NewHandler(
            (_, _, _) => { Interlocked.Increment(ref fileCount); return Task.CompletedTask; }, store: store);
        (byte[] goodBody, string goodSig) = SignedBody(ValidPayload(eventId: "evt-x", component: "sdk-js"));
        BugReportHandlerResult accepted = await mapped.HandleAsync(goodBody, goodSig, CancellationToken.None);

        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal(1, fileCount);
    }

    [Fact]
    public async Task BoundsSlowFiling_AcksWithDeadlineReason()
    {
        BugReportWebhookHandler handler = NewHandler(
            onAccepted: async (_, _, token) => await Task.Delay(Timeout.InfiniteTimeSpan, token),
            deadline: TimeSpan.FromMilliseconds(50));
        (byte[] body, string signature) = SignedBody(ValidPayload());

        BugReportHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("accepted-filing-deadline", result.Reason);
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
            },
            deadline: TimeSpan.FromSeconds(30));
        (byte[] body, string signature) = SignedBody(ValidPayload());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(body, signature, shutdown.Token));
    }
}
