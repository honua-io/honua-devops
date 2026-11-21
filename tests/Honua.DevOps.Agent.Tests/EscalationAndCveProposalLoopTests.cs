using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public sealed class EscalationAndCveProposalLoopTests
{
    private const string WebhookSecret = "escalation-replay-test-secret";

    [Fact]
    public async Task EscalationHandler_RejectsStaleAndFutureTimestamps_WhenReplayWindowConfigured()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
        EscalationWebhookHandler handler = new(
            WebhookSecret,
            replayWindow: TimeSpan.FromMinutes(5),
            now: () => now);

        (byte[] staleBody, string staleSig) = BuildSignedEscalation("evt-stale", now.AddMinutes(-10));
        WebhookHandlerResult stale = await handler.HandleAsync(staleBody, staleSig, CancellationToken.None);
        Assert.Equal(400, stale.StatusCode);
        Assert.Equal("stale-timestamp", stale.Reason);

        (byte[] futureBody, string futureSig) = BuildSignedEscalation("evt-future", now.AddMinutes(10));
        WebhookHandlerResult future = await handler.HandleAsync(futureBody, futureSig, CancellationToken.None);
        Assert.Equal(400, future.StatusCode);
        Assert.Equal("stale-timestamp", future.Reason);

        (byte[] missingTsBody, string missingTsSig) = BuildSignedEscalation("evt-no-ts", escalatedAt: null);
        WebhookHandlerResult missingTs = await handler.HandleAsync(missingTsBody, missingTsSig, CancellationToken.None);
        Assert.Equal(400, missingTs.StatusCode);
        Assert.Equal("missing-escalated-at", missingTs.Reason);
    }

    [Fact]
    public async Task EscalationHandler_DeduplicatesRetriedEventId_AndLeavesFailedCallbackRetryable()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
        int invocations = 0;
        bool shouldFail = true;
        InMemoryEventIdempotencyStore store = new(TimeSpan.FromMinutes(10), () => now);

        EscalationWebhookHandler handler = new(
            WebhookSecret,
            onAccepted: (_, _) =>
            {
                invocations++;
                if (shouldFail)
                {
                    throw new InvalidOperationException("transient downstream error");
                }
                return Task.CompletedTask;
            },
            replayWindow: TimeSpan.FromMinutes(5),
            idempotencyStore: store,
            now: () => now);

        (byte[] body, string sig) = BuildSignedEscalation("evt-retry-42", now.AddSeconds(-30));

        // 1. Transient callback failure does NOT claim the eventId
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(body, sig, CancellationToken.None));
        Assert.False(store.IsProcessed("evt-retry-42"));

        // 2. Retry succeeds and claims the eventId
        shouldFail = false;
        WebhookHandlerResult accepted = await handler.HandleAsync(body, sig, CancellationToken.None);
        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal("accepted", accepted.Reason);
        Assert.True(store.IsProcessed("evt-retry-42"));

        // 3. Duplicate redelivery is idempotently rejected with 409 without invoking callback again
        WebhookHandlerResult duplicate = await handler.HandleAsync(body, sig, CancellationToken.None);
        Assert.Equal(409, duplicate.StatusCode);
        Assert.Equal("duplicate-event", duplicate.Reason);
        Assert.Equal(2, invocations);
    }

    [Fact]
    public void WebhookListenerConfiguration_LoadsOptionalReplayWindowSeconds()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_WEBHOOK_SECRET", "secret");
        scope.Set("HONUA_DEVOPS_WEBHOOK_REPLAY_WINDOW_SECONDS", "300");

        WebhookListenerConfiguration config = WebhookListenerConfiguration.Load();
        Assert.Equal(300, config.ReplayWindowSeconds);
    }

    [Fact]
    public void CvePatchProposalBuilder_RejectsMutableTagsAndMismatchedCfnImageUri()
    {
        string validDigest = "ghcr.io/honua-io/honua-server@sha256:" + new string('1', 64);
        string otherDigest = "ghcr.io/honua-io/honua-server@sha256:" + new string('2', 64);

        Assert.Throws<ArgumentException>(() => CvePatchProposalBuilder.Build(new CvePatchProposalInput(
            TicketId: "ST-20261120-0001",
            CveId: "CVE-2026-12345",
            ReleaseVersion: "2026.1.4",
            ServerImageDigest: "ghcr.io/honua-io/honua-server:2026.1.4",
            SigstoreReleaseLockRef: "sha256:" + new string('a', 64))));

        Assert.Throws<ArgumentException>(() => CvePatchProposalBuilder.Build(new CvePatchProposalInput(
            TicketId: "ST-20261120-0001",
            CveId: "CVE-2026-12345",
            ReleaseVersion: "2026.1.4",
            ServerImageDigest: validDigest,
            SigstoreReleaseLockRef: "sha256:" + new string('a', 64),
            CloudFormationServerImageUri: otherDigest)));
    }

    [Fact]
    public async Task ProposeCustomerFixAsync_ViaSupportAndDirectBreakGlass_WritesTamperEvidentAuditChain()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "honua-cve-proposal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string auditFile = Path.Combine(tempDir, "audit.jsonl");
        string auditHookTarget = $"file://{auditFile}";
        const string chainKey = "devops-cve-chain-key-2026";

        string serverDigest = "ghcr.io/honua-io/honua-server@sha256:" + new string('a', 64);
        string workerDigest = "ghcr.io/honua-io/honua-worker@sha256:" + new string('b', 64);

        CvePatchProposal proposal1 = CvePatchProposalBuilder.Build(new CvePatchProposalInput(
            TicketId: "ST-20261120-0010",
            CveId: "CVE-2026-54321",
            ReleaseVersion: "2026.1.6",
            ServerImageDigest: serverDigest,
            SigstoreReleaseLockRef: "sigstore://release/platform-lock.2026.1.6.sig",
            WorkerImageDigest: workerDigest,
            CloudFormationStackName: "honua-prod-stack"));

        Assert.Equal("cve-image-patch", proposal1.ProposalKind);
        Assert.Equal(serverDigest, proposal1.CloudFormationServerImageUri);
        Assert.Equal(serverDigest, proposal1.CloudFormationParameters["ServerImageUri"]);
        Assert.Equal(workerDigest, proposal1.CloudFormationParameters["WorkerImageUri"]);
        Assert.Contains("ParameterKey=ServerImageUri,ParameterValue=" + serverDigest, proposal1.ChangeSetInstructions, StringComparison.Ordinal);

        TestHttpMessageHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/propose-ops-action", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { proposal = new { proposalId = "prop-support-101" } });
            }
            return TestHttpMessageHandler.JsonOk(new { proposalId = "prop-direct-202" });
        });

        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendConfiguration config = new(
            HonuaApiBaseUri: new Uri("http://localhost:8080"),
            OTelBaseUri: new Uri("http://localhost:4318"),
            HonuaApiKey: null,
            OTelApiKey: null,
            HonuaReadinessPath: "healthz/ready",
            OTelHealthPath: "",
            HonuaAdminErrorsPath: "api/v1/admin/observability/errors",
            HonuaAdminTelemetryPath: "api/v1/admin/observability/telemetry",
            HonuaMetricsHealthPath: "api/v1/metrics/health",
            HonuaMetricsPerformancePath: "api/v1/metrics/performance",
            HonuaMetricsDatabasePath: "api/v1/metrics/database",
            HonuaMetricsCachePath: "api/v1/metrics/cache",
            HonuaMetricsMemoryPath: "api/v1/metrics/memory",
            HonuaQueryCacheStatisticsPath: "api/v1/admin/performance/database/query-cache/statistics",
            HonuaAdminVersionPath: "api/v1/admin/version",
            HonuaAdminCapabilitiesPath: "api/v1/admin/capabilities",
            HonuaManifestExportPath: "api/v1/admin/manifest",
            HonuaManifestApplyPath: "api/v1/admin/manifest/apply",
            RequestTimeout: TimeSpan.FromSeconds(5),
            SupportApiBaseUri: new Uri("https://support.honua.test"),
            SupportApiBearerToken: "support-operator-token");

        using SupportGateway gateway = new(config, httpClient);
        await using (JsonlAuditSink sink = JsonlAuditSink.ForFile(auditFile))
        {
            // 1. Submit via honua-support
            CustomerFixProposalResult supportResult = await gateway.ProposeCustomerFixAsync(proposal1, sink, chainKey);
            Assert.True(supportResult.IsSuccess);
            Assert.Equal("prop-support-101", supportResult.ProposalId);
            Assert.NotEmpty(supportResult.AuditEntryHash);

            // 2. Submit directly to customer's honua-server Ops Loop using break-glass ops:propose token
            const string breakGlassToken = "super-secret-breakglass-propose-token";
            CustomerFixProposalResult directResult = await gateway.ProposeCustomerFixDirectAsync(
                new Uri("https://customer.honua.example"),
                breakGlassToken,
                proposal1,
                sink,
                chainKey);
            Assert.True(directResult.IsSuccess);
            Assert.Equal("prop-direct-202", directResult.ProposalId);
            Assert.NotEqual(supportResult.AuditEntryHash, directResult.AuditEntryHash);

            // Reject insecure HTTP for non-local direct break-glass endpoint
            await Assert.ThrowsAsync<ArgumentException>(() => gateway.ProposeCustomerFixDirectAsync(
                new Uri("http://customer.honua.example"),
                breakGlassToken,
                proposal1,
                sink,
                chainKey));
        }

        // Verify break-glass token never leaked into the audit journal
        string auditContent = File.ReadAllText(auditFile);
        Assert.DoesNotContain("super-secret-breakglass-propose-token", auditContent, StringComparison.Ordinal);

        // Verify OperationJournal can find the recorded entries
        OperationSearchResult search = OperationJournal.Find(
            auditHookTarget, CvePatchProposalBuilder.AuditToolName, mutatedOnly: null, statusContains: null, limit: 10);
        Assert.Equal(2, search.Matched);

        // Verify cryptographic chain integrity
        CveProposalAuditVerification intact = CvePatchProposalBuilder.VerifyJournalChain(auditHookTarget, chainKey);
        Assert.True(intact.IsIntact);
        Assert.Equal(2, intact.EntryCount);

        // Tamper with line 1 and confirm VerifyJournalChain detects corruption
        string[] lines = File.ReadAllLines(auditFile);
        lines[0] = lines[0].Replace("CVE-2026-54321", "CVE-2026-00000", StringComparison.Ordinal);
        File.WriteAllLines(auditFile, lines);

        CveProposalAuditVerification tampered = CvePatchProposalBuilder.VerifyJournalChain(auditHookTarget, chainKey);
        Assert.False(tampered.IsIntact);
        Assert.Equal(1, tampered.FirstCorruptedIndex);
    }

    private static (byte[] Body, string Signature) BuildSignedEscalation(string eventId, DateTimeOffset? escalatedAt)
    {
        object payload = new
        {
            eventId,
            eventType = "ticket.escalation_requested",
            ticketId = "ST-20261120-0099",
            customerId = "cust-1",
            severity = "critical",
            environment = "prod",
            service = "honua-server",
            phase = "escalation-requested",
            customerStatus = "escalated",
            sla = new { supportTier = "paid-pilot" },
            escalation = escalatedAt is null
                ? null
                : new { tier = 1, accessMode = "operator-scoped", escalatedAt }
        };
        string json = JsonSerializer.Serialize(payload, EscalationWebhookPayload.JsonOptions);
        byte[] body = Encoding.UTF8.GetBytes(json);
        string sig = WebhookSignatureVerifier.ComputeSignatureHeader(WebhookSecret, json);
        return (body, sig);
    }
}
