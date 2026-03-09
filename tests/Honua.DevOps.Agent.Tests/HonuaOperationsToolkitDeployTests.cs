using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class HonuaOperationsToolkitDeployTests
{
    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ThrowsOnInvalidEnvironmentInput()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "qa",
                "main",
                "sync",
                "release",
                CancellationToken.None));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ThrowsOnInvalidActionInput()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "dev",
                "main",
                "sync;rm",
                "release",
                CancellationToken.None));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ThrowsOnUnsafeServiceName()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api;drop",
                "dev",
                "main",
                "sync",
                "release",
                CancellationToken.None));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_SanitizesPayloadAndCommands()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        OperationRuntime runtime = CreateRuntime();
        HonuaOperationsToolkit toolkit = new(runtime, gateway);

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "dev",
            revision: "feature/main",
            action: "dryrun",
            changeSummary: "deploy\u0000 now",
            cancellationToken: CancellationToken.None);

        CapturedRequest applyRequest = Assert.Single(
            handler.CapturedRequests,
            request => request.Method == HttpMethod.Post.Method);
        using JsonDocument requestJson = JsonDocument.Parse(applyRequest.Body!);
        string serialized = requestJson.RootElement.ToString();

        Assert.Contains("\"changeSummary\":\"deploy now\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0000", serialized, StringComparison.Ordinal);
        Assert.Contains("\"executionPolicyRef\"", serialized, StringComparison.Ordinal);
        Assert.NotNull(response.Evidence);
        Assert.NotNull(response.GitOpsPlan);
        Assert.NotNull(response.ReleaseOrchestration);
        Assert.NotNull(response.ServiceBundleReconciliation);
        Assert.True(response.Evidence!.DryRun);
        Assert.Equal("plan-only", response.Evidence.PolicyGate);
        Assert.Equal("pr-first", response.Evidence.ApprovalMode);
        Assert.Equal("stdout-evidence", response.Evidence.AuditHookTarget);
        Assert.Equal("disabled", response.Evidence.SupportSessionAccess);
        Assert.Equal(60, response.Evidence.SupportSessionTtlMinutes);
        Assert.True(response.Evidence.SupportSessionCustomerVisible);
        Assert.True(response.Evidence.BreakGlassPostActionReviewRequired);
        Assert.Contains(response.Actions, action => action.Contains("Adapter plan infra (eks):", StringComparison.Ordinal));
        Assert.Contains(response.ValidationChecks, check => check.Equals("adapter-verify:eks", StringComparison.Ordinal));
        Assert.Contains(response.ValidationChecks, check => check.Equals("gitops-drift:service-state", StringComparison.Ordinal));
        Assert.Equal("manifest-export", response.GitOpsPlan!.ActualStateSource);
        Assert.Contains(response.GitOpsPlan.SupportedOperations, operation => operation == "diff");
        Assert.Equal("actual-state-pending", Assert.Single(response.GitOpsPlan.Environments).DiffStatus);
        Assert.Contains(response.ReleaseOrchestration!.Stages, stage => stage.Kind == ReleaseStageKind.Smoke);
        Assert.Equal("single-environment-auto-advance", response.ReleaseOrchestration.PromotionPolicy.Gate);
        Assert.Contains(response.ServiceBundleReconciliation!.Operations, operation => operation.Surface == "metadata-subset-apply");
        Assert.Equal("manifest-export-plus-capability-probes", response.ServiceBundleReconciliation.ExportMode);
        Assert.Contains(response.ServiceBundleReconciliation.DriftScopes, scope => scope.Scope == "service-state");
        Assert.Contains(response.Evidence.RequiredChecks, item => item == "service-state-export");
        Assert.Contains(response.Actions, action =>
            action.Contains("honua-gitops plan --service roads-api --env dev --revision feature/main", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ParsesExportedActualRevisionIntoGitOpsPlan()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Get
                ? TestHttpMessageHandler.JsonOk(new
                {
                    items = new object[]
                    {
                        new
                        {
                            metadata = new
                            {
                                @namespace = "dev",
                                annotations = new Dictionary<string, string>
                                {
                                    ["honua.devops/revision"] = "release/2026.02"
                                }
                            },
                            spec = new
                            {
                                deployment = new
                                {
                                    service = "roads-api",
                                    environment = "dev",
                                    revision = "release/2026.02"
                                }
                            }
                        }
                    }
                })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "dev",
            revision: "release/2026.03",
            action: "plan",
            changeSummary: "compare desired revision",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.GitOpsPlan);
        Assert.Equal("desired=release/2026.03; in-sync=0; revision-diff=1; actual-state-pending=0", response.GitOpsPlan!.DiffSummary);
        Assert.Equal("release/2026.02", Assert.Single(response.GitOpsPlan.Environments).ActualRevision);
        Assert.Equal("dev=release/2026.02", response.Evidence!.CurrentRevision);
    }

    [Fact]
    public async Task PlanGitOpsEngineAsync_UsesSnapshotsOnlyAndEmitsTypedPlan()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.EndsWith("/api/v1/admin/manifest", StringComparison.Ordinal) == true)
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    items = new object[]
                    {
                        new
                        {
                            metadata = new
                            {
                                @namespace = "dev",
                                annotations = new Dictionary<string, string>
                                {
                                    ["honua.devops/revision"] = "release/2026.02"
                                }
                            },
                            spec = new
                            {
                                deployment = new
                                {
                                    service = "roads-api",
                                    environment = "dev",
                                    revision = "release/2026.02"
                                }
                            }
                        },
                        new
                        {
                            metadata = new
                            {
                                @namespace = "staging"
                            },
                            spec = new
                            {
                                deployment = new
                                {
                                    service = "roads-api",
                                    environment = "staging",
                                    revision = "release/2026.01"
                                }
                            }
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.EndsWith("/api/v1/admin/capabilities", StringComparison.Ordinal) == true)
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    features = new[]
                    {
                        "connections",
                        "publishing",
                        "styles"
                    }
                });
            }

            return TestHttpMessageHandler.JsonOk(new { status = "unexpected-request" });
        });
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanGitOpsEngineAsync(
            service: "roads-api",
            environmentsCsv: "dev,staging",
            revision: "release/2026.03",
            action: "plan",
            changeSummary: "review engine diff before rollout",
            cancellationToken: CancellationToken.None);

        Assert.Equal("gitops-engine-plan", response.Status);
        Assert.NotNull(response.GitOpsPlan);
        Assert.NotNull(response.ReleaseOrchestration);
        Assert.NotNull(response.ServiceBundleReconciliation);
        Assert.NotNull(response.Evidence);
        Assert.DoesNotContain(
            handler.CapturedRequests,
            request => request.Method == HttpMethod.Post.Method);
        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Equal("manifest-export", response.GitOpsPlan!.ActualStateSource);
        Assert.Equal("desired=release/2026.03; in-sync=0; revision-diff=2; actual-state-pending=0", response.GitOpsPlan.DiffSummary);
        Assert.Contains(response.GitOpsPlan.SupportedOperations, operation => operation == "approve");
        Assert.Equal("release/2026.02", response.GitOpsPlan.Environments.Single(environment => environment.Environment == "dev").ActualRevision);
        Assert.Equal("release/2026.01", response.GitOpsPlan.Environments.Single(environment => environment.Environment == "staging").ActualRevision);
        Assert.Equal("gitops-engine-plan", response.Evidence!.PolicyGate);
        Assert.Equal("plan-only", response.Evidence.GateStatus);
        Assert.Contains(
            response.Actions,
            action => action.Contains("honua-gitops diff --service roads-api --env dev --revision release/2026.03", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_RejectsProdInExecuteLowerEnvTier()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "prod",
                "main",
                "apply",
                "release",
                CancellationToken.None));

        Assert.Contains("execute-lower-env", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_RequiresPromoteActionForProdPromotionTier()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.PromoteProd),
            gateway,
            DirectAllowedPolicy());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "prod",
                "main",
                "apply",
                "release",
                CancellationToken.None));

        Assert.Contains("promote", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ExecutesProdPromotionWhenTierAllowsIt()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.PromoteProd),
            gateway,
            DirectAllowedPolicy());

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "staging,prod",
            revision: "release/2026.03",
            action: "promote",
            changeSummary: "promote validated release",
            cancellationToken: CancellationToken.None);

        CapturedRequest applyRequest = Assert.Single(
            handler.CapturedRequests,
            request => request.Method == HttpMethod.Post.Method);
        using JsonDocument requestJson = JsonDocument.Parse(applyRequest.Body!);
        string serialized = requestJson.RootElement.ToString();

        Assert.Equal("execute-enabled", response.Status);
        Assert.Contains("\"dryRun\":false", serialized, StringComparison.Ordinal);
        Assert.Contains("\"honua.devops/execution-tier\":\"promote-prod\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"promotionRef\"", serialized, StringComparison.Ordinal);
        Assert.NotNull(response.Evidence);
        Assert.NotNull(response.GitOpsPlan);
        Assert.NotNull(response.ReleaseOrchestration);
        Assert.NotNull(response.ServiceBundleReconciliation);
        Assert.False(response.Evidence!.DryRun);
        Assert.Equal("promote", response.Evidence.EffectiveAction);
        Assert.Equal("prod-promotion-gated", response.Evidence.PolicyGate);
        Assert.Contains("approval-record", response.Evidence.RequiredChecks);
        Assert.Equal("prod-promotion-gated", response.GitOpsPlan!.GateStatus);
        Assert.Contains(response.GitOpsPlan.Environments, environment => environment.Environment == "prod" && environment.GateStatus == "promotion-approval");
        Assert.Contains(response.ReleaseOrchestration!.Stages, stage => stage.Kind == ReleaseStageKind.Promote);
        Assert.Equal("promotion-approval", response.ReleaseOrchestration.PromotionPolicy.Gate);
        Assert.Contains(response.ServiceBundleReconciliation!.Operations, operation => operation.Surface == "connections");
        Assert.Equal("direct-allowed", response.Evidence.ApprovalMode);
        Assert.Equal("stdout-evidence", response.Evidence.AuditHookTarget);
        Assert.Equal("disabled", response.Evidence.SupportSessionAccess);
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_BlocksDirectExecutionWhenApprovalModeIsPrFirst()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            OperatorPolicyModel.Default);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "dev",
                "release/2026.03",
                "apply",
                "direct execution without approval",
                CancellationToken.None));

        Assert.Contains("pr-first", exception.Message, StringComparison.Ordinal);
    }

    private static OperationRuntime CreateRuntime(
        string gitOpsTool = "honua-gitops",
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            gitOpsTool,
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"]);
    }

    private static BackendGateway CreateGateway(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        TestHttpMessageHandler handler = new(responder);
        HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

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

    private static OperatorPolicyModel DirectAllowedPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }
}
