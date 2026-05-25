using System.Net;
using System.Net.Http;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class ConsoleOperationBridgeTests
{
    [Fact]
    public void BuildProposalIdempotencyKey_IsDeterministicAndOrderInsensitive()
    {
        string first = ConsoleOperationBridge.BuildProposalIdempotencyKey(
            "prod-api", "roads-api", ["staging", "dev"], "release/2026.03", "sync");
        string second = ConsoleOperationBridge.BuildProposalIdempotencyKey(
            "prod-api", "roads-api", ["dev", "staging"], "release/2026.03", "sync");

        Assert.Equal(first, second);
        Assert.Equal("honua-devops:proposal:prod-api:roads-api:dev-staging:release/2026.03:sync", first);
    }

    [Fact]
    public void BuildProposalIdempotencyKey_HashesWhenTooLong()
    {
        string longRevision = new('a', 180);
        string key = ConsoleOperationBridge.BuildProposalIdempotencyKey(
            "prod-api", "roads-api", ["dev"], longRevision, "sync");

        Assert.StartsWith("honua-devops:proposal:prod-api:", key, StringComparison.Ordinal);
        Assert.DoesNotContain(longRevision, key, StringComparison.Ordinal);
        Assert.True(key.Length <= 200, $"key length was {key.Length}");

        // Hash branch stays deterministic for identical scope.
        string repeat = ConsoleOperationBridge.BuildProposalIdempotencyKey(
            "prod-api", "roads-api", ["dev"], longRevision, "sync");
        Assert.Equal(key, repeat);
    }

    [Fact]
    public async Task CreateGitOpsProposalAsync_RecordsProposalWithoutExecuting()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "AwaitingApproval" });
            }

            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.CreateGitOpsProposalAsync(
            service: "roads-api",
            environmentsCsv: "dev",
            revision: "release/2026.03",
            action: "sync",
            changeSummary: "ship roads-api",
            owner: "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Equal("proposed", proposal.Status);
        Assert.Equal("op-7f3", proposal.OperationId);
        Assert.Equal("propose", proposal.EffectiveAction);
        Assert.Equal("honua-devops:proposal:prod-api:roads-api:dev:release/2026.03:sync", proposal.IdempotencyKey);

        // Calls preflight, plan, and create-operation only.
        Assert.Contains(handler.CapturedRequests, request =>
            request.Method == "GET" && request.Uri.Contains("/deploy/preflight", StringComparison.Ordinal));
        Assert.Contains(handler.CapturedRequests, request =>
            request.Method == "POST" && request.Uri.Contains("/deploy/plan", StringComparison.Ordinal));
        CapturedRequest createRequest = Assert.Single(
            handler.CapturedRequests,
            request => request.Method == "POST" && request.Uri.EndsWith("/deploy/operations", StringComparison.Ordinal));

        // The operation is recorded but not submitted/executed.
        using JsonDocument body = JsonDocument.Parse(createRequest.Body!);
        Assert.False(body.RootElement.GetProperty("submitImmediately").GetBoolean());

        // No manifest apply, submit, or rollback ever happens from the proposal path.
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/manifest/apply", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));

        Assert.NotNull(response.BackendSteps);
        OperationBackendStep createStep = Assert.Single(
            response.BackendSteps!,
            step => step.Name == "deploy-operation-create");
        Assert.True(createStep.MutatesState);
    }

    [Fact]
    public async Task CreateGitOpsProposalAsync_SurfacesGovernedSuggestionsThatRequireApproval()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "AwaitingApproval" })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.CreateGitOpsProposalAsync(
            "roads-api", "dev", "main", "sync", "ship", "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        SuggestedAction submit = Assert.Single(proposal.SuggestedActions, action => action.Id == "submit-operation");
        Assert.True(submit.RequiresApproval);
        Assert.True(submit.MutatesState);
        Assert.Equal("op-7f3", submit.TargetOperationId);
        Assert.Equal("governed-submit", submit.Kind);

        SuggestedAction review = Assert.Single(proposal.SuggestedActions, action => action.Id == "review-evidence");
        Assert.False(review.RequiresApproval);
        Assert.False(review.MutatesState);

        // Workflow deep links are assembled from configured base URLs.
        WorkflowLink consoleLink = Assert.Single(proposal.WorkflowLinks, link => link.Rel == "self");
        Assert.True(consoleLink.Available);
        Assert.Contains("op-7f3", consoleLink.Href!, StringComparison.Ordinal);
        Assert.Contains(proposal.Evidence, evidence => evidence.Type == "deploy-operation" && evidence.RawRef == "deploy-operation:op-7f3");
    }

    [Fact]
    public async Task CreateGitOpsProposalAsync_StaysBlockedWhenTargetUnconfigured()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: null);

        OperationResponse response = await bridge.CreateGitOpsProposalAsync(
            "roads-api", "dev", "main", "sync", "ship", "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Equal("target-unconfigured", proposal.Status);
        Assert.Null(proposal.OperationId);
        // No durable operation id is invented, and no backend call is made.
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task CreateGitOpsProposalAsync_ReportsContractUnavailableWhenNoOperationIdReturned()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.CreateGitOpsProposalAsync(
            "roads-api", "dev", "main", "sync", "ship", "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Equal("contract-unavailable", proposal.Status);
        Assert.Null(proposal.OperationId);
        OperationBackendStep createStep = Assert.Single(
            response.BackendSteps!,
            step => step.Name == "deploy-operation-create");
        Assert.False(createStep.MutatesState);
    }

    [Theory]
    [InlineData("Planned", "planned", "plan")]
    [InlineData("AwaitingApproval", "awaiting-approval", "approval")]
    [InlineData("Submitted", "submitted", "execution")]
    [InlineData("Reconciling", "reconciling", "execution")]
    [InlineData("Succeeded", "succeeded", "complete")]
    [InlineData("Failed", "failed", "complete")]
    [InlineData("RollbackRequested", "rollback-requested", "rollback")]
    [InlineData("RolledBack", "rolled-back", "rollback")]
    [InlineData("ManualInterventionRequired", "manual-intervention-required", "blocked")]
    public async Task GetDevOpsOperationStatusAsync_MapsServerStatuses(
        string serverStatus,
        string expectedStatus,
        string expectedPhase)
    {
        TestHttpMessageHandler handler = new(_ =>
            TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = serverStatus, providerOperationId = "prov-1" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.GetDevOpsOperationStatusAsync("op-7f3");

        DevOpsOperationStatus status = AssertStatus(response);
        Assert.Equal(expectedStatus, status.Status);
        Assert.Equal(expectedPhase, status.Phase);
        Assert.Equal("op-7f3", status.OperationId);
        Assert.Equal("prov-1", status.ProviderOperationId);

        // The same operationId follows every section; PR/CI are evidence-missing, not scraped.
        Assert.Equal("evidence-missing", status.Pr.Status);
        Assert.Equal("evidence-missing", status.Ci.Status);
        Assert.Equal("evidence-missing", status.Smoke.Status);
        Assert.Equal("evidence-missing", status.SloWatch.Status);
        Assert.All(
            new[] { status.Proposal, status.Promotion, status.RollbackReadiness, status.RollbackExecution },
            stage => Assert.All(stage.Evidence, evidence => Assert.Equal("deploy-operation:op-7f3", evidence.RawRef)));
    }

    [Fact]
    public async Task GetDevOpsOperationStatusAsync_FlagsUnknownWhenServerCallFails()
    {
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.GetDevOpsOperationStatusAsync("op-7f3");

        DevOpsOperationStatus status = AssertStatus(response);
        Assert.Equal("unknown", status.Status);
        Assert.NotEmpty(status.BlockingReasons);
        OperationBackendStep step = Assert.Single(response.BackendSteps!);
        Assert.False(step.MutatesState);
    }

    [Fact]
    public async Task BuildAiDevOpsBriefAsync_IsAdvisoryWithEvidenceAndGatedActions()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.BuildAiDevOpsBriefAsync(
            service: "roads-api",
            environment: "prod",
            title: "Elevated 5xx after deploy",
            summary: "Error rate climbed after release/2026.03.",
            recommendedAction: "Roll back to release/2026.02 and investigate.",
            evidenceReference: "otel://logs/roads-api/2026-05-25",
            operationId: "op-7f3",
            confidence: "high");

        Assert.Equal("advisory", response.Status);
        Assert.Contains("advisory-brief-no-auto-apply", response.ValidationChecks);
        // Advisory brief never touches the backend.
        Assert.Empty(handler.CapturedRequests);

        AiDevOpsBrief brief = AssertBrief(response);
        Assert.Equal("advisory", brief.Status);
        Assert.Equal("high", brief.Confidence);
        Assert.Equal("op-7f3", brief.OperationId);
        Assert.Contains(brief.Evidence, evidence => evidence.RawRef == "otel://logs/roads-api/2026-05-25");
        Assert.Contains(brief.Evidence, evidence => evidence.Type == "deploy-operation" && evidence.RawRef == "deploy-operation:op-7f3");
        Assert.Contains(brief.WorkflowLinks, link => link.Rel == "server-operation" && link.Available);
        Assert.Contains(brief.AffectedResources, resource => resource is { Kind: "service", Name: "roads-api" });

        SuggestedAction submit = Assert.Single(brief.SuggestedActions, action => action.Id == "submit-operation");
        Assert.True(submit.RequiresApproval);
        Assert.True(submit.MutatesState);
        SuggestedAction advisory = Assert.Single(brief.SuggestedActions, action => action.Id == "review-recommendation");
        Assert.False(advisory.MutatesState);
    }

    [Fact]
    public async Task BuildAiDevOpsBriefAsync_MarksEvidenceMissingWhenNothingSupplied()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.BuildAiDevOpsBriefAsync(
            service: "roads-api",
            environment: "prod",
            title: "Capacity review",
            summary: "Routine review.",
            recommendedAction: "Monitor.",
            evidenceReference: string.Empty,
            operationId: string.Empty,
            confidence: "unrecognized");

        AiDevOpsBrief brief = AssertBrief(response);
        Assert.Equal("medium", brief.Confidence);
        Assert.Null(brief.OperationId);
        EvidenceRef evidence = Assert.Single(brief.Evidence);
        Assert.Equal("evidence-missing", evidence.Type);
        Assert.DoesNotContain(brief.SuggestedActions, action => action.MutatesState);
    }

    [Fact]
    public async Task GetGitOpsProposalAsync_ProjectsExistingServerOperation()
    {
        // Mirrors the landed honua-server DeployOperationResponse shape: revisions and the
        // echoed create-request parameters live under "target", not at the response root.
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new
        {
            operationId = "op-7f3",
            status = "AwaitingApproval",
            target = new
            {
                targetId = "prod-api",
                environment = "prod",
                currentRevision = "release/2026.02",
                desiredRevision = "release/2026.03",
                parameters = new { service = "roads-api", environments = "dev,staging", action = "sync", owner = "soleil" }
            }
        }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.GetGitOpsProposalAsync("op-7f3");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Equal("proposed", proposal.Status);
        Assert.Equal("op-7f3", proposal.OperationId);
        Assert.Equal("roads-api", proposal.Service);
        Assert.Equal("sync", proposal.RequestedAction);
        Assert.Equal("soleil", proposal.Owner);
        Assert.Equal("release/2026.03", proposal.DesiredRevision);
        Assert.Equal("release/2026.02", proposal.CurrentRevision);
        Assert.Equal(["dev", "staging"], proposal.TargetEnvironments);
        Assert.Contains(proposal.SuggestedActions, action => action.Kind == "governed-submit");
    }

    [Fact]
    public async Task GetGitOpsProposalAsync_DoesNotExposeMutatingActionsWhenOperationMissing()
    {
        // A not-found / contract-unavailable read must not advertise submit/rollback or a
        // server-operation link against an operation that does not exist.
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.GetGitOpsProposalAsync("op-missing");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Equal("contract-unavailable", proposal.Status);
        Assert.Null(proposal.OperationId);
        Assert.DoesNotContain(proposal.SuggestedActions, action => action.MutatesState);
        Assert.DoesNotContain(
            proposal.SuggestedActions,
            action => action.Kind is "governed-submit" or "governed-rollback");
        Assert.DoesNotContain(
            proposal.WorkflowLinks,
            link => link.Rel is "server-operation" or "submit" or "rollback");
        // Only non-mutating review guidance remains, with no fabricated deep links.
        Assert.Contains(proposal.SuggestedActions, action => action.Id == "review-evidence");
        Assert.All(proposal.WorkflowLinks, link => Assert.False(link.Available));
    }

    [Fact]
    public async Task ConsoleBridgeProjection_IsInProcessOnly_AndOmittedFromSerializedResponse()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "AwaitingApproval" })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.CreateGitOpsProposalAsync(
            "roads-api", "dev", "main", "sync", "ship", "soleil");

        // The typed projection is available in-process by object reference...
        Assert.NotNull(response.ConsoleBridge);
        Assert.NotNull(response.ConsoleBridge!.Proposal);

        // ...but, like GitOpsPlan, it is excluded from the LLM-facing wire shape (JsonIgnore),
        // so the compact status/summary is all the model and audit journal serialize.
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.False(document.RootElement.TryGetProperty("ConsoleBridge", out _));
        Assert.True(document.RootElement.TryGetProperty("Status", out _));
        Assert.True(document.RootElement.TryGetProperty("Summary", out _));
    }

    [Fact]
    public async Task GetDevOpsOperationStatusAsync_ValidatesOperationId()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bridge.GetDevOpsOperationStatusAsync("has spaces"));
    }

    private static GitOpsProposalBridge AssertProposal(OperationResponse response)
    {
        Assert.NotNull(response.ConsoleBridge);
        Assert.Equal("gitops-proposal", response.ConsoleBridge!.Kind);
        Assert.NotNull(response.ConsoleBridge.Proposal);
        return response.ConsoleBridge.Proposal!;
    }

    private static DevOpsOperationStatus AssertStatus(OperationResponse response)
    {
        Assert.NotNull(response.ConsoleBridge);
        Assert.Equal("operation-status", response.ConsoleBridge!.Kind);
        Assert.NotNull(response.ConsoleBridge.OperationStatus);
        return response.ConsoleBridge.OperationStatus!;
    }

    private static AiDevOpsBrief AssertBrief(OperationResponse response)
    {
        Assert.NotNull(response.ConsoleBridge);
        Assert.Equal("ai-devops-brief", response.ConsoleBridge!.Kind);
        Assert.NotNull(response.ConsoleBridge.Brief);
        return response.ConsoleBridge.Brief!;
    }

    private static ConsoleOperationBridge CreateBridge(TestHttpMessageHandler handler, string? deployTargetId)
    {
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        return new ConsoleOperationBridge(CreateRuntime(deployTargetId), gateway, DirectAllowedPolicy());
    }

    private static OperationRuntime CreateRuntime(string? deployTargetId)
    {
        return new OperationRuntime(
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: deployTargetId);
    }

    private static OperatorPolicyModel DirectAllowedPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }

    private static BackendConfiguration CreateBackendConfiguration()
    {
        return new BackendConfiguration(
            HonuaApiBaseUri: new Uri("http://localhost:8080"),
            OTelBaseUri: new Uri("http://localhost:4318"),
            HonuaApiKey: null,
            OTelApiKey: null,
            HonuaReadinessPath: "healthz/ready",
            OTelHealthPath: "health",
            OTelLogsPath: "v1/logs/search",
            OTelMetricsPath: "v1/metrics/search",
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
            ConsoleBaseUri: new Uri("https://console.honua.test"));
    }
}
