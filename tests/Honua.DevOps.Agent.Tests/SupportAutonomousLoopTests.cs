using System.Net;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public sealed class SupportAutonomousLoopTests
{
    [Fact]
    public async Task PostPatchStatusAsync_SendsPatchLifecycleV1PayloadAndEncodesTicketId()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { stage = "released" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateConfig(), httpClient);

        PatchStatusUpdate update = new()
        {
            Stage = "released",
            Fingerprint = "fp-ogc-500",
            Component = "server",
            BuildChannel = "patch",
            BuildArtifactRef = "ghcr.io/honua-io/honua-server:2026.1.4",
            BuildArtifactDigest = "sha256:abcdef0123456789",
            ReleaseVersion = "2026.1.4",
            FixedInServerVersion = "2026.1.4",
            Scorecard = new PatchScorecardUpdate
            {
                RegressionTestAdded = true,
                FailedBeforePatch = true,
                PassedAfterPatch = true,
                CiPassed = true
            }
        };

        BackendCallResult result = await gateway.PostPatchStatusAsync("TKT/42", update);

        Assert.True(result.IsSuccess);
        CapturedRequest req = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", req.Method);
        Assert.Contains("/api/v1/tickets/TKT%2F42/patch-status", req.Uri, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(req.Body!);
        Assert.Equal("released", doc.RootElement.GetProperty("stage").GetString());
        Assert.Equal("sha256:abcdef0123456789", doc.RootElement.GetProperty("buildArtifactDigest").GetString());
        Assert.True(doc.RootElement.GetProperty("scorecard").GetProperty("failedBeforePatch").GetBoolean());
    }

    [Fact]
    public async Task PostPatchStatusAsync_RejectsBuildCutWithoutPassingTestFirstScorecard()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { }));
        using HttpClient httpClient = new(handler);
        using SupportGateway gateway = new(CreateConfig(), httpClient);

        PatchStatusUpdate invalid = new()
        {
            Stage = "build-cut",
            Scorecard = new PatchScorecardUpdate { RegressionTestAdded = true, FailedBeforePatch = false }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.PostPatchStatusAsync("TKT-1", invalid));
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task CollectAwsObservabilityAndProposeOpsFix_RouteToSupportEndpointsOnly()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { id = "ok-1" }));
        using HttpClient httpClient = new(handler);
        using SupportGateway gateway = new(CreateConfig(), httpClient);

        BackendCallResult awsRes = await gateway.CollectAwsObservabilityAsync(
            "TKT-8",
            new AwsObservabilityCollectRequest { Scopes = ["cloudwatch", "alb"], CloudWatchNamespace = "AWS/ApplicationELB" });
        BackendCallResult propRes = await gateway.ProposeOpsFixAsync(
            "TKT-8",
            new OpsLoopFixProposalRequest
            {
                FindingId = "finding-dlq-01",
                ActionId = "RedriveDeadLetters",
                Rationale = "ALB target group healthy; safe to propose redrive.",
                AwsEvidenceIds = ["awsev-123"]
            });

        Assert.True(awsRes.IsSuccess);
        Assert.True(propRes.IsSuccess);
        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Contains("/api/v1/tickets/TKT-8/aws-collect", handler.CapturedRequests[0].Uri, StringComparison.Ordinal);
        Assert.Contains("/api/v1/tickets/TKT-8/propose-ops-fix", handler.CapturedRequests[1].Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.CapturedRequests, r => r.Uri.Contains("/approve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostTicketMessageAndPublishSecurityAdvisory_SendExpectedContracts()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { ok = true }));
        using HttpClient httpClient = new(handler);
        using SupportGateway gateway = new(CreateConfig(), httpClient);

        BackendCallResult msgRes = await gateway.PostTicketMessageAsync(
            "TKT-9",
            new SupportTicketMessageRequest
            {
                Body = "Please approve scoped diagnostics so we can verify the fix.",
                ActionPrompt = new TicketMessageActionPrompt { Kind = "approve-access", Label = "Approve access" }
            });
        BackendCallResult advRes = await gateway.PublishSecurityAdvisoryAsync(
            new SecurityAdvisoryPublishRequest
            {
                AdvisoryId = "ADV-2026-0042",
                CveId = "CVE-2026-4242",
                Title = "Critical parser fix",
                Summary = "Upgrade to 2026.1.5",
                Component = "honua-server",
                Severity = "critical",
                CisaKev = true,
                FixedVersion = "2026.1.5",
                FixedDigest = "sha256:fixeddigest123",
                Embargoed = true
            });

        Assert.True(msgRes.IsSuccess);
        Assert.True(advRes.IsSuccess);
        Assert.Contains("/api/v1/tickets/TKT-9/messages", handler.CapturedRequests[0].Uri, StringComparison.Ordinal);
        Assert.Contains("/api/v1/security/advisories", handler.CapturedRequests[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BugReportReporter_AfterFiling_ReportsQueuedPatchStatusToHonuaSupport()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { stage = "queued" }));
        using HttpClient httpClient = new(handler);
        using SupportGateway gateway = new(CreateConfig(), httpClient);
        StubIssueTracker tracker = new();
        BugReportReporter reporter = new(tracker, ["bug"], new StringWriter(), new StringWriter())
        {
            SupportGateway = gateway
        };

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), new RepoRef("honua-io", "honua-server"), CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.Filed, outcome);
        CapturedRequest req = Assert.Single(handler.CapturedRequests);
        Assert.Contains("/api/v1/tickets/ST-2026-0099/patch-status", req.Uri, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(req.Body!);
        Assert.Equal("queued", doc.RootElement.GetProperty("stage").GetString());
        Assert.Equal("fp-closed-loop-1", doc.RootElement.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task BugReportReporter_SupportCallbackFailure_DoesNotFailConfirmedIssueFiling()
    {
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using HttpClient httpClient = new(handler);
        using SupportGateway gateway = new(CreateConfig(), httpClient);
        StringWriter stderr = new();
        BugReportReporter reporter = new(new StubIssueTracker(), ["bug"], new StringWriter(), stderr)
        {
            SupportGateway = gateway
        };

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), new RepoRef("honua-io", "honua-server"), CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.Filed, outcome);
        Assert.Contains("patch-status callback failed", stderr.ToString(), StringComparison.Ordinal);
    }

    private static BackendConfiguration CreateConfig() =>
        BackendConfigurationFixture.Create() with
        {
            SupportApiBaseUri = new Uri("https://support.example.test"),
            SupportApiTicketsPath = "api/v1/tickets",
            SupportApiBearerToken = "operator-test-key"
        };

    private static BugReport SampleReport() =>
        new(
            EventId: "evt-99",
            EmittedAt: DateTimeOffset.Parse("2026-11-27T12:00:00Z"),
            TicketId: "ST-2026-0099",
            Component: "server",
            Severity: "high",
            Environment: "prod",
            Service: "ogc-features",
            Title: "OGC collections 500",
            Summary: "Sanitized repro confirmed.",
            Fingerprint: "fp-closed-loop-1",
            EnvelopeRefs: ["env-1"],
            FixtureRefs: ["fx-1"],
            TicketUrl: "https://support.example.test/tickets/ST-2026-0099");

    private sealed class StubIssueTracker : IIssueTracker
    {
        public bool IsEnabled => true;

        public Task<IssueSearchResult> FindOpenIssueAsync(RepoRef repo, string searchToken, CancellationToken cancellationToken) =>
            Task.FromResult(new IssueSearchResult(true, false, null, "ok"));

        public Task<IssueFilingResult> FileIssueAsync(RepoRef repo, GeneratedIssue issue, CancellationToken cancellationToken) =>
            Task.FromResult(new IssueFilingResult(true, "https://github.com/honua-io/honua-server/issues/9999", "created"));
    }
}
