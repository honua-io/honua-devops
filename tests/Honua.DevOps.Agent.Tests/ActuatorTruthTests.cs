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
            edition: "enterprise");

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

    [Fact]
    public async Task AutoRemediation_DriftObserve_ReportsObservedWithNoMutatingStep()
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

        Assert.Equal("auto-remediation-observed", response.Status);
        Assert.All(response.BackendSteps!, step => Assert.False(step.MutatesState));
    }

    // ---- Every executed claim carries the full evidence ----

    [Fact]
    public async Task RunbookExecute_DeploySubmit_ExecutedClaimCarriesReceiptAndMutatingStep()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-9", status = "Succeeded" })
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
        Assert.Contains(response.Findings, finding => finding.Contains("Actuator receipt: op-9", StringComparison.Ordinal));
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
