using System.Net.Http;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// End-to-end response truth for the write-capable tools (issue #151).
//
// The bug these replace: readiness was derived from `autoApply`, the execution mode/tier,
// and the approval mode BEFORE the typed runbook/remediation router proved an actuator
// exists — so a fully write-enabled posture reported ready while making zero backend calls.
public class ActuatorTruthTests
{
    // ---- Unknown / unimplemented actions ----

    [Theory]
    [InlineData("clear-tile-cache")]
    [InlineData("restart-everything")]
    [InlineData("manifest-apply")]
    public async Task RunbookExecute_UnknownRunbook_ReportsUnsupportedActionAndCallsNothing(string runbook)
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: runbook,
            service: "roads-api",
            environment: "staging",
            parameters: "layer=roads",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal(ActuationOutcome.UnsupportedAction, response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
    }

    [Fact]
    public async Task AutoRemediation_AutoApplyWithNoTypedActuator_RefusesWithoutMutating()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "cache miss storm",
            desiredOutcome: "restore p95 latency",
            autoApply: true,
            edition: "enterprise");

        Assert.Equal(ActuationOutcome.UnsupportedAction, response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
    }

    // Issue #156 REQ-002. The old classifier substring-matched "drift" in the free text, so a
    // prose description was enough to select the drift actuator. Intent is now typed: an
    // unmapped server rule refuses even when the description is full of the old trigger word.
    [Fact]
    public async Task AutoRemediation_UnmappedFindingWithDriftWordingInProse_RefusesWithoutBackendCall()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "manifest drift detected: drift everywhere, drift drift",
            desiredOutcome: "observe the drift and converge desired state",
            autoApply: true,
            edition: "enterprise",
            findingId: "serving-latency-slo-breach-9a1b2c3d4e5f60718293a4b5c6d7e8f9");

        Assert.Equal(ActuationOutcome.UnsupportedAction, response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("serving-latency-slo-breach", StringComparison.Ordinal));
    }

    // Free-text prose with no typed intent at all is not a classification either.
    [Fact]
    public async Task AutoRemediation_DriftProseWithoutTypedIntent_RefusesWithoutBackendCall()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "manifest drift detected",
            desiredOutcome: "converge desired state",
            autoApply: true,
            edition: "enterprise");

        Assert.Equal(ActuationOutcome.UnsupportedAction, response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
    }

    // A finding id and an action name that disagree are ambiguous; the agent refuses rather
    // than picking a winner, and it refuses before any backend call.
    [Fact]
    public async Task AutoRemediation_FindingIdAndActionDisagree_RefusesWithoutBackendCall()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "operationId=deploy-123",
            desiredOutcome: "restore health",
            autoApply: true,
            edition: "enterprise",
            findingId: "platform-release-skew-0123456789abcdef0123456789abcdef",
            remediationAction: RemediationAction.GitOpsRollback);

        Assert.Equal(ActuationOutcome.UnsupportedAction, response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
    }

    // The rollback family actuates a NAMED durable operation. Without that identity the
    // request stays unresolved instead of reaching the rollback actuator with an empty target.
    [Fact]
    public async Task AutoRemediation_RollbackFindingWithoutOperationId_RefusesWithoutBackendCall()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "deploy stuck",
            desiredOutcome: "recover the deployment",
            autoApply: true,
            edition: "enterprise",
            findingId: "deploy-manual-intervention-fedcba9876543210fedcba9876543210");

        Assert.Equal(ActuationOutcome.UnsupportedAction, response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
    }

    // ---- Supported action, no mutation requested or permitted ----

    [Fact]
    public async Task RunbookExecute_MutatingRunbookUnderPlanPosture_ReportsPlanReadyAndCallsNothing()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, PlanRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-submit",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=op-1",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-plan-ready", response.Status);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task RunbookExecute_NotConfirmed_ReportsConfirmationRequiredAndCallsNothing()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-preflight",
            service: "roads-api",
            environment: "dev",
            parameters: string.Empty,
            confirmed: false,
            edition: "enterprise");

        Assert.Equal("confirmation-required", response.Status);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task AutoRemediation_SupportedActionWithoutApproval_ReportsApprovalRequiredAndCallsNothing()
    {
        TestHttpMessageHandler handler = Handler();
        // pr-first: the actuator exists, but nothing may run without governed approval.
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), OperatorPolicyModel.Default);

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "manifest drift detected",
            desiredOutcome: "converge desired state",
            autoApply: true,
            edition: "enterprise",
            findingId: "platform-release-skew-0123456789abcdef0123456789abcdef");

        Assert.Equal("auto-remediation-approval-required", response.Status);
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);
    }

    // ---- Read-only actuators run, but never claim execution ----

    [Fact]
    public async Task RunbookExecute_ReadOnlyRunbook_ReportsObservedWithNoMutatingStep()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "manifest-drift",
            service: "roads-api",
            environment: "dev",
            parameters: string.Empty,
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-observed", response.Status);
        Assert.NotNull(response.BackendSteps);
        Assert.All(response.BackendSteps!, step => Assert.False(step.MutatesState));
        Assert.DoesNotContain(response.BackendSteps!, step => step.MutatesState);
        Assert.All(handler.CapturedRequests, request => Assert.Equal("GET", request.Method));
    }

    [Theory]
    // Both release-divergence rules resolve to the read-only drift report...
    [InlineData("platform-release-skew-0123456789abcdef0123456789abcdef", "")]
    [InlineData("platform-release-runtime-divergence-0123456789abcdef0123456789abcdef", "")]
    // ...a bare rule id carries the same classification as the full finding id...
    [InlineData("platform-release-skew", "")]
    // ...and the explicit typed action name reaches the same actuator.
    [InlineData("", RemediationAction.DriftObserve)]
    public async Task AutoRemediation_DriftObserve_ReportsObservedWithNoMutatingStep(
        string findingId,
        string remediationAction)
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "declared release is not co-versioned",
            desiredOutcome: "converge desired state",
            autoApply: true,
            edition: "enterprise",
            findingId: findingId,
            remediationAction: remediationAction);

        Assert.Equal("auto-remediation-observed", response.Status);
        Assert.All(response.BackendSteps!, step => Assert.False(step.MutatesState));
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("honua.manifest-drift.read", StringComparison.Ordinal));
    }

    // The mapped rollback family reaches the mutating deploy-control actuator, with its
    // durable receipt and a successful mutating backend step — nothing less claims applied.
    [Fact]
    public async Task AutoRemediation_DeployManualInterventionFinding_RoutesToGovernedRollback()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/rollback", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "deploy-123", status = "RolledBack" })
                : TestHttpMessageHandler.JsonOk(new
                {
                    operationId = "deploy-123",
                    status = "Succeeded",
                    metadataRelease = new { rollbackPlan = new { @class = "MetadataOnly", isDataAffecting = false } }
                }));

        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "operationId=deploy-123",
            desiredOutcome: "recover the stuck deployment",
            autoApply: true,
            edition: "enterprise",
            findingId: "deploy-manual-intervention-fedcba9876543210fedcba9876543210");

        Assert.Equal("auto-remediation-applied", response.Status);
        Assert.Contains(response.BackendSteps!, step => step.MutatesState && step.Success);
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("honua.deploy-operation.rollback", StringComparison.Ordinal));
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("deploy-manual-intervention", StringComparison.Ordinal));
    }

    // Resolving intent from a server-owned finding id changes WHICH actuator is selected and
    // nothing else: rollback stays disabled in the default posture, and the shared capability
    // refusal still lands before any backend call.
    [Fact]
    public async Task AutoRemediation_RollbackFindingUnderDefaultPosture_StaysCapabilityRefused()
    {
        TestHttpMessageHandler handler = Handler();
        HonuaOperationsToolkit toolkit = Toolkit(
            handler,
            ExecuteRuntime() with { RollbackEnabled = false },
            DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "operationId=deploy-123",
            desiredOutcome: "recover the stuck deployment",
            autoApply: true,
            edition: "enterprise",
            findingId: "deploy-manual-intervention-fedcba9876543210fedcba9876543210");

        Assert.Equal(ActuationOutcome.ExperimentalDisabled, response.Status);
        Assert.Empty(handler.CapturedRequests);
    }

    // ---- Every executed claim carries the full evidence ----

    [Fact]
    public async Task RunbookExecute_DeploySubmit_ExecutedClaimCarriesReceiptAndMutatingStep()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post
                ? TestHttpMessageHandler.JsonOk(new
                {
                    operationId = "op-9",
                    status = "Succeeded",
                    // A terminal success must carry the authoritative actuator receipt bound
                    // to this operation; without it the executor fails closed to
                    // `indeterminate` and no execution claim is possible.
                    actuatorReceipt = new { receiptId = "rcpt-9", operationId = "op-9" }
                })
                : TestHttpMessageHandler.JsonOk(new { operationId = "op-9", status = "Submitted" }));

        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-submit",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=op-9",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-executed", response.Status);
        Assert.NotNull(response.BackendSteps);
        Assert.Contains(response.BackendSteps!, step => step.MutatesState && step.Success);
        Assert.Contains(response.Findings, finding => finding.Contains("Actuator receipt: rcpt-9", StringComparison.Ordinal));
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("honua.deploy-operation.submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunbookExecute_DeploySubmitParkedForApproval_DoesNotClaimExecution()
    {
        TestHttpMessageHandler handler = new(_ =>
            TestHttpMessageHandler.JsonOk(new { operationId = "op-9", status = "AwaitingApproval" }));

        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-submit",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=op-9",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-approval-required", response.Status);
        // The control plane parked it, so no submit was ever issued.
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.EndsWith("/submit", StringComparison.Ordinal));
        Assert.DoesNotContain(response.BackendSteps ?? [], step => step.MutatesState && step.Success);
    }

    [Fact]
    public async Task RunbookExecute_DeploySubmitBackendFailure_ReportsContractUnavailableNotExecuted()
    {
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });

        HonuaOperationsToolkit toolkit = Toolkit(handler, ExecuteRuntime(), DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-submit",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=op-9",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("contract-unavailable", response.Status);
        Assert.DoesNotContain(response.BackendSteps ?? [], step => step.MutatesState && step.Success);
    }

    // ---- The registry is the single write authority per action ----

    [Fact]
    public void EachRegisteredActionResolvesToExactlyOneActuator()
    {
        foreach (string name in ActuatorRegistry.RegisteredRunbookNames)
        {
            Assert.True(ActuatorRegistry.TryResolveRunbook(name, out ActuatorDescriptor descriptor));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.ActuatorId));
        }

        // Aliases resolve to the SAME actuator id: two names, one write authority.
        ActuatorRegistry.TryResolveRunbook("rollback", out ActuatorDescriptor viaAlias);
        ActuatorRegistry.TryResolveRunbook("deploy-rollback", out ActuatorDescriptor viaCanonical);
        Assert.Equal(viaCanonical.ActuatorId, viaAlias.ActuatorId);
    }

    // ---- helpers ----

    private static TestHttpMessageHandler Handler()
        => new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));

    private static HonuaOperationsToolkit Toolkit(
        TestHttpMessageHandler handler,
        OperationRuntime runtime,
        OperatorPolicyModel policy)
    {
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendGateway gateway = new(BackendConfigurationFixture.Create(), httpClient);
        return new HonuaOperationsToolkit(runtime, gateway, policy);
    }

    private static OperationRuntime ExecuteRuntime()
        => OperationRuntime.SafeDefault with
        {
            ExecutionMode = ExecutionMode.Execute,
            ExecutionTier = ExecutionTier.ExecuteLowerEnv,
            DeployTargetId = "prod-api",
            RollbackEnabled = true
        };

    private static OperationRuntime PlanRuntime()
        => OperationRuntime.SafeDefault with { DeployTargetId = "prod-api", RollbackEnabled = true };

    private static OperatorPolicyModel DirectAllowedPolicy()
        => new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
}
