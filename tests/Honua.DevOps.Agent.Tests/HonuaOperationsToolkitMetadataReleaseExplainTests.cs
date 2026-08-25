using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// Issue #57 (AI DevOps explanation): explain_metadata_release_changeset summarizes and explains
// the proposed metadata-release GitOps PR operation from a validated release package WITHOUT
// mutating state. These tests assert the read-only, backend-free, deterministic posture and that
// the structured explanation carries the proposed branch, files-by-kind, semantic resources,
// compatibility verdict, and rollback posture. They mirror the change-set builder / plan-tool
// fixtures and the toolkit test construction patterns.
public sealed class HonuaOperationsToolkitMetadataReleaseExplainTests
{
    private const string ReadyPackage = """
    {
      "releaseId": "rel-2026-03-meta",
      "service": "roads-api",
      "desiredRevision": "release/2026.03",
      "targetEnvironments": ["staging", "prod"],
      "semanticResources": [
        { "kind": "Layer", "name": "roads", "action": "upsert" },
        { "kind": "Style", "name": "roads-default", "action": "upsert" }
      ],
      "compatibility": { "status": "compatible", "breakingChanges": 0, "warnings": 0 },
      "scriptCoverage": { "covered": 2, "total": 2, "uncovered": [] },
      "rollbackPolicy": { "classification": "automatic", "knownGoodRevision": "release/2026.02" }
    }
    """;

    private const string BlockedPackage = """
    {
      "releaseId": "rel-bad",
      "service": "roads-api",
      "desiredRevision": "release/2026.04",
      "targetEnvironments": ["prod"],
      "compatibility": { "status": "incompatible", "breakingChanges": 2, "warnings": 0 },
      "rollbackPolicy": { "classification": "manual", "knownGoodRevision": "release/2026.03" }
    }
    """;

    private const string SecretPackage = """
    {
      "releaseId": "rel-secret",
      "service": "roads-api",
      "desiredRevision": "release/2026.06",
      "targetEnvironments": ["staging"],
      "semanticResources": [
        { "kind": "Layer", "name": "roads api_key=super-secret-value", "action": "upsert" }
      ],
      "compatibility": { "status": "compatible" },
      "rollbackPolicy": { "classification": "irreversible" }
    }
    """;

    [Fact]
    public async Task ExplainMetadataReleaseChangeSetAsync_ReadyPackage_ExplainsProposedPrOperationReadOnly()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.ExplainMetadataReleaseChangeSetAsync(
            ReadyPackage,
            CancellationToken.None);

        // Strictly read-only: no backend call of any kind was made.
        Assert.Empty(handler.CapturedRequests);

        Assert.Equal(MetadataChangeSetReadiness.Ready, response.Status);
        Assert.Contains("roads-api", response.Summary, StringComparison.Ordinal);
        Assert.Contains("ready to open", response.Summary, StringComparison.Ordinal);

        // The change set is carried for in-process callers (the Console bridge surface).
        Assert.NotNull(response.MetadataReleaseChangeSet);
        Assert.Equal("rel-2026-03-meta", response.MetadataReleaseChangeSet!.ReleasePackageId);

        // The proposed PR operation is summarized: branch, files-by-kind, semantic resources.
        Assert.Contains(response.Findings, finding => finding.StartsWith("Proposed branch: honua-devops/metadata-release/roads-api-", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.StartsWith("Section `pr-operation`:", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.StartsWith("Section `semantic-resources`:", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.StartsWith("Section `compatibility-and-coverage`:", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.StartsWith("Section `rollback`:", StringComparison.Ordinal));

        Assert.Contains(response.ValidationChecks, check => check == "changeset-explanation-read-only-no-mutation");
        Assert.Contains(response.Actions, action => action.Contains("never writes Git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplainMetadataReleaseChangeSetAsync_BlockedPackage_SurfacesBlockingAndRefusesPr()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.ExplainMetadataReleaseChangeSetAsync(
            BlockedPackage,
            CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);
        Assert.Equal(MetadataChangeSetReadiness.Blocked, response.Status);
        Assert.Contains(response.Findings, finding => finding.StartsWith("Blocking:", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.StartsWith("Do not open a PR", StringComparison.Ordinal));
        Assert.Contains(response.Risks, risk => risk.Contains("blocked by the supplied compatibility report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplainMetadataReleaseChangeSetAsync_KeepsSecretsOutOfExplanation()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.ExplainMetadataReleaseChangeSetAsync(
            SecretPackage,
            CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);

        IEnumerable<string> surfaced = new[] { response.Summary }
            .Concat(response.Findings)
            .Concat(response.Actions)
            .Concat(response.ValidationChecks)
            .Concat(response.Risks);
        Assert.All(surfaced, text => Assert.DoesNotContain("super-secret-value", text, StringComparison.Ordinal));

        // Irreversible rollback posture is surfaced as a forward-fix-only risk.
        Assert.Contains(response.Risks, risk => risk.Contains("irreversible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplainMetadataReleaseChangeSetAsync_InvalidJson_ReturnsGracefulUnknownResponse()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.ExplainMetadataReleaseChangeSetAsync(
            "{ not valid json",
            CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);
        Assert.Equal(MetadataChangeSetReadiness.Unknown, response.Status);
        Assert.Null(response.MetadataReleaseChangeSet);
        Assert.Contains(response.Findings, finding => finding.StartsWith("Parse error:", StringComparison.Ordinal));
        Assert.Contains(response.ValidationChecks, check => check == "changeset-explanation-read-only-no-mutation");
    }

    [Fact]
    public async Task ExplainMetadataReleaseChangeSetAsync_SameInputTwice_IsDeterministicAndBackendFree()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse first = await toolkit.ExplainMetadataReleaseChangeSetAsync(ReadyPackage, CancellationToken.None);
        OperationResponse second = await toolkit.ExplainMetadataReleaseChangeSetAsync(ReadyPackage, CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Findings, second.Findings);
        Assert.Equal(first.Actions, second.Actions);
        Assert.Equal(first.ValidationChecks, second.ValidationChecks);
        Assert.Equal(first.Risks, second.Risks);
    }

    private static OperationRuntime CreateRuntime(
        string gitOpsTool = "honua-gitops",
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan,
        string? deployTargetId = null)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            gitOpsTool,
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: deployTargetId);
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
            RequestTimeout: TimeSpan.FromSeconds(5));
    }
}
