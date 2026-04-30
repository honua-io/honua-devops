using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.OrchestrationHost;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public sealed class AzureOrchestrationHostPlannerTests
{
    [Fact]
    public void Build_AnalyzeWorkflowMapsMcpAndGrpcBoundaries()
    {
        OrchestrationHostPlan plan = AzureOrchestrationHostPlanner.Build(
            OperatorWorkflowFamily.Analyze,
            environment: "staging",
            operatorGoal: "Identify flood-risk parcels and produce a map package",
            packageReference: null,
            deploymentTarget: null,
            publishExternally: false,
            runtime: CreateRuntime(),
            policy: OperatorPolicyModel.Default);

        string[] expectedStages =
        [
            "capture-intent",
            "ground-candidates",
            "clarify",
            "compile-plan",
            "validate-plan",
            "dry-run",
            "execute",
            "compose-map",
            "return-result-package"
        ];

        Assert.Equal(OperatorWorkflowFamily.Analyze, plan.WorkflowFamily);
        Assert.Equal(expectedStages, plan.Stages.Select(stage => stage.Stage.ToConfigValue()).ToArray());
        Assert.Contains(plan.ContractSurfaces, surface => surface.Contains("geospatial-mcp", StringComparison.Ordinal));
        Assert.Contains(plan.ContractSurfaces, surface => surface.Contains("geospatial-grpc", StringComparison.Ordinal));
        Assert.Contains(plan.BoundaryRules, rule => rule.Contains("Do not redefine MCP", StringComparison.Ordinal));
        Assert.Contains(plan.RequiredChecks, check => check == "map-package-contract");
    }

    [Fact]
    public void Build_DeployWorkflowRequiresApprovalForExternalPublication()
    {
        OperatorPolicyModel policy = new(
            ApprovalMode.PrFirst,
            "audit://azure-host",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);

        OrchestrationHostPlan plan = AzureOrchestrationHostPlanner.Build(
            OperatorWorkflowFamily.Deploy,
            environment: "prod",
            operatorGoal: "Deploy the reviewed flood app package",
            packageReference: "app-package:flood-review@2026.04",
            deploymentTarget: "azure-container-apps",
            publishExternally: true,
            runtime: CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.PromoteProd),
            policy: policy);

        OrchestrationHostStagePlan publishStage = Assert.Single(
            plan.Stages,
            stage => stage.Stage == OrchestrationStageKind.Publish);

        Assert.Equal("approval-required", plan.GateStatus);
        Assert.Equal("approval-required", publishStage.Status);
        Assert.Contains("approval-record", publishStage.RequiredChecks);
        Assert.Contains("deployment-state-contract", plan.RequiredChecks);
        Assert.DoesNotContain(plan.Stages, stage => stage.Stage == OrchestrationStageKind.ComposeMap);
    }

    [Fact]
    public async Task PlanAzureOperatorWorkflowAsync_EmitsDryRunEvidenceAndTypedPlan()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.PromoteProd),
            gateway);

        OperationResponse response = await toolkit.PlanAzureOperatorWorkflowAsync(
            workflowFamily: "deploy",
            environment: "prod",
            operatorGoal: "Publish app-package:flood-review to the hosted Azure surface",
            packageReference: "app-package:flood-review@2026.04",
            deploymentTarget: "azure-container-apps",
            publishExternally: true);

        Assert.Equal("orchestration-plan-ready", response.Status);
        Assert.NotNull(response.OrchestrationHost);
        Assert.Equal(OperatorWorkflowFamily.Deploy, response.OrchestrationHost!.WorkflowFamily);
        Assert.NotNull(response.Evidence);
        Assert.True(response.Evidence!.DryRun);
        Assert.Equal("approval-required", response.Evidence.PolicyGate);
        Assert.Contains("approval-record", response.Evidence.RequiredChecks);
        Assert.Contains(response.Actions, action => action.Contains("publish", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsUnsupportedWorkflowFamily()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => OperatorWorkflowFamilyExtensions.Parse("edit-source-data"));

        Assert.Contains("Allowed values: analyze, publish, build, deploy", exception.Message, StringComparison.Ordinal);
    }

    private static OperationRuntime CreateRuntime(
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["azure-functions", "aks", "aca"]);
    }

    private static BackendGateway CreateGateway()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
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
