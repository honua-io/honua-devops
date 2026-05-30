using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Tests;

public class ReleasePackageExplainerTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "release-packages");

    [Fact]
    public async Task ExplainReadyPackage_IsReadyWithCleanSectionsAndNoMutation()
    {
        OperationResponse response = await ExplainFixtureAsync("ready.json", mode: "explanation");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("ready", explanation.Readiness);
        Assert.Equal("explanation", explanation.Mode);
        Assert.Equal("roads-api", explanation.Service);
        Assert.Equal("op-ready-1", explanation.OperationId);
        Assert.Equal(["dev", "staging"], explanation.TargetEnvironments);
        Assert.Equal("automatic", explanation.RollbackClassification);
        Assert.Empty(explanation.BlockingReasons);

        // Every section interprets supplied evidence; none is evidence-missing.
        Assert.All(explanation.Sections, section => Assert.NotEqual("evidence-missing", section.Status));
        Assert.Contains(explanation.Sections, section => section is { Section: "compatibility", Status: "ready" });
        Assert.Contains(explanation.Sections, section => section is { Section: "promotion-gates", Status: "ready" });

        // Read-only explanation mode never surfaces a mutating suggestion.
        Assert.DoesNotContain(explanation.SuggestedActions, action => action.MutatesState);
        Assert.Contains(response.ValidationChecks, check => check == "explanation-read-only-no-compute");
    }

    [Fact]
    public async Task ExplainWarningPackage_IsWarningWithResidualRiskAndAdvisoryGateUnmet()
    {
        OperationResponse response = await ExplainFixtureAsync("warning.json", mode: "explanation");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("warning", explanation.Readiness);
        Assert.NotEmpty(explanation.ResidualRisks);
        Assert.Empty(explanation.BlockingReasons);

        // A non-blocking gate is unmet -> warning, not blocked.
        PromotionGate scriptsGate = Assert.Single(explanation.PromotionGates, gate => gate.Id == "scripts-covered");
        Assert.False(scriptsGate.Satisfied);
        Assert.False(scriptsGate.Blocking);
    }

    [Fact]
    public async Task ExplainBlockedPackage_IsBlockedWithBlockingReasonsAndNoProposalHandoff()
    {
        OperationResponse response = await ExplainFixtureAsync("blocked.json", mode: "proposal");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("blocked", explanation.Readiness);
        Assert.NotEmpty(explanation.BlockingReasons);
        Assert.Contains(explanation.Sections, section => section is { Section: "compatibility", Status: "blocked" });
        Assert.Contains(explanation.Sections, section => section is { Section: "pr-preview", Status: "blocked" });

        // Even in proposal mode, a blocked release never offers a PR-creation handoff.
        Assert.DoesNotContain(explanation.SuggestedActions, action => action.Kind == "governed-proposal");
    }

    [Fact]
    public async Task ExplainUnknownPackage_IsUnknownWhenSignalsAreAbsent()
    {
        OperationResponse response = await ExplainFixtureAsync("unknown.json", mode: "explanation");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("unknown", explanation.Readiness);
        Assert.Equal("unknown", explanation.RollbackClassification);
        Assert.Null(explanation.OperationId);
    }

    [Fact]
    public async Task ExplainRollbackRequiredPackage_ClassifiesRollbackAndOffersGovernedRollback()
    {
        OperationResponse response = await ExplainFixtureAsync("rollback-required.json", mode: "explanation");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("rollback-required", explanation.Readiness);
        Assert.Equal("automatic", explanation.RollbackClassification);
        Assert.NotEmpty(explanation.BlockingReasons);

        // A governed rollback suggestion is surfaced but stays approval-gated and never executes.
        SuggestedAction rollback = Assert.Single(
            explanation.SuggestedActions,
            action => action.Kind == "governed-rollback");
        Assert.True(rollback.RequiresApproval);
        Assert.True(rollback.MutatesState);
    }

    [Fact]
    public async Task ProposalMode_ReadyPackage_OffersGovernedApprovalRequiredHandoffOnly()
    {
        OperationResponse response = await ExplainFixtureAsync("ready.json", mode: "proposal");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("proposal", explanation.Mode);
        SuggestedAction handoff = Assert.Single(
            explanation.SuggestedActions,
            action => action.Kind == "governed-proposal");
        Assert.True(handoff.RequiresApproval);
        Assert.True(handoff.MutatesState);
    }

    [Fact]
    public async Task MalformedDocument_ReturnsUnknownProjectionWithoutThrowing()
    {
        ReleasePackageExplainer explainer = new(TestBackendConfiguration());

        OperationResponse response = await explainer.ExplainReleasePackageAsync(
            "{ not json", mode: "explanation", correlationId: "console:abc");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("unknown", explanation.Readiness);
        Assert.NotEmpty(explanation.BlockingReasons);
        EvidenceRef missing = Assert.Single(explanation.Evidence);
        Assert.Equal("evidence-missing", missing.Type);
    }

    [Fact]
    public async Task SecretsInEvidence_AreRedactedFromTheProjection()
    {
        // A compatibility finding that accidentally carries a token must never be surfaced
        // verbatim; the bridge scrubs all free text passing through the explanation.
        string payload = JsonSerializer.Serialize(new
        {
            releaseId = "rel-secret",
            service = "secret-api",
            desiredRevision = "release/2026.03",
            compatibility = new
            {
                status = "compatible",
                breakingChanges = 0,
                warnings = 0,
                findings = new[] { "context url https://api/x?api_key=SUPERSECRET123 captured" }
            }
        });

        ReleasePackageExplainer explainer = new(TestBackendConfiguration());
        OperationResponse response = await explainer.ExplainReleasePackageAsync(
            payload, mode: "explanation", correlationId: "console:redact");

        ReleaseExplanation explanation = AssertExplanation(response);
        string serialized = JsonSerializer.Serialize(explanation);
        Assert.DoesNotContain("SUPERSECRET123", serialized, StringComparison.Ordinal);
        Assert.Contains("redacted", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrelationId_IsPreservedAndProjectionStaysInProcessOnly()
    {
        OperationResponse response = await ExplainFixtureAsync(
            "ready.json", mode: "explanation", correlationId: "console:checkout:42");

        ReleaseExplanation explanation = AssertExplanation(response);
        Assert.Equal("console:checkout:42", explanation.CorrelationId);

        // Like the other bridge projections, the structured explanation is in-process only
        // and is excluded from the LLM-facing/audit wire shape.
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.False(document.RootElement.TryGetProperty("ConsoleBridge", out _));
        Assert.True(document.RootElement.TryGetProperty("Status", out _));
    }

    private static async Task<OperationResponse> ExplainFixtureAsync(
        string fixture,
        string mode,
        string correlationId = "console:test")
    {
        string json = await File.ReadAllTextAsync(Path.Combine(FixtureRoot, fixture));
        ReleasePackageExplainer explainer = new(TestBackendConfiguration());
        return await explainer.ExplainReleasePackageAsync(json, mode, correlationId);
    }

    private static ReleaseExplanation AssertExplanation(OperationResponse response)
    {
        Assert.NotNull(response.ConsoleBridge);
        Assert.Equal("release-explanation", response.ConsoleBridge!.Kind);
        Assert.NotNull(response.ConsoleBridge.ReleaseExplanation);
        return response.ConsoleBridge.ReleaseExplanation!;
    }

    private static BackendConfiguration TestBackendConfiguration()
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
