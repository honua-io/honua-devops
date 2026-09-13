using System.Net.Http;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// End-to-end proof for the bounded deployment recovery path (issue #191): a
// DeploymentRecoveryGrant minted at approval time, later re-verified and actuated through
// RecoveryExecutor. Unlike RollbackExecutor's tests (a fresh caller-supplied operationId/
// reason each call), every scenario here starts from an already-sealed grant, proving the
// declared-at-approval-time scope -- not a fresh model decision -- gates the compensation.
public class RecoveryExecutorTests
{
    [Fact]
    public async Task ExecuteRecoveryAsync_CapabilityDisabled_IssuesNothing()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        ActuationSpine spine = new(ExecuteRuntime(protectedRecoveryEnabled: false), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);
        RecoveryExecutor executor = new(ExecuteRuntime(protectedRecoveryEnabled: false), gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ExperimentalDisabled, result.Status);
        Assert.False(result.Mutated);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_ValidGrant_RestoresPriorRevisionAndQuarantinesCandidate()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/rollback", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-recover-1", status = "RolledBack" });
            }

            return TestHttpMessageHandler.JsonOk(new { operationId = "op-recover-1", status = "Succeeded", candidateRevision = "release/2026.03" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(protectedRecoveryEnabled: true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, result.Status);
        Assert.True(result.Mutated);
        Assert.Contains(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Contains("Restored desired revision: release/2026.02", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Contains("Quarantined rejected revision: release/2026.03", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_WrongActor_RefusesAndCallsNoMutatingRoute()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { operationId = "op-recover-1", status = "Succeeded", candidateRevision = "release/2026.03" }));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(protectedRecoveryEnabled: true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            grant, "someone-else@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_CandidateHasMovedSinceApproval_RefusesAndCallsNoMutatingRoute()
    {
        // The operation now shows a different candidate than the one this grant was approved
        // for -- some other approved intent has already superseded it. Recovery must not
        // overwrite that newer intent.
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { operationId = "op-recover-1", status = "Succeeded", candidateRevision = "release/2026.05" }));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(protectedRecoveryEnabled: true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_RetryAfterCrash_IssuesExactlyOneCompensation()
    {
        // Simulates a restart/retry immediately after the mutating call: the SAME grant is
        // presented to a second RecoveryExecutor (a fresh process would resume this way), but
        // the shared ActuationSpine ledger still holds the original claim, so the second
        // attempt observes it instead of issuing a duplicate rollback.
        int rollbackCalls = 0;
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/rollback", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref rollbackCalls);
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-recover-1", status = "RolledBack" });
            }

            return TestHttpMessageHandler.JsonOk(new { operationId = "op-recover-1", status = "Succeeded", candidateRevision = "release/2026.03" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(protectedRecoveryEnabled: true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        RecoveryExecutor first = new(runtime, gateway, DirectAllowedPolicy(), spine);
        GitOpsExecutionResult firstResult = await first.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        RecoveryExecutor retry = new(runtime, gateway, DirectAllowedPolicy(), spine);
        GitOpsExecutionResult retryResult = await retry.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, firstResult.Status);
        Assert.True(firstResult.Mutated);
        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, retryResult.Status);
        Assert.False(retryResult.Mutated);
        Assert.Equal(1, rollbackCalls);
    }

    // ---- helpers ----

    private static ActuationSpine.DeploymentRecoveryGrant IssueGrant(ActuationSpine spine)
    {
        Assert.True(spine.AuthorizeRecoveryGrant(
            new ActuationRequest(
                ActuatorId: "honua.deploy-operation.recovery",
                Action: "recover",
                Target: "prod-api",
                Environments: ["prod"],
                DesiredState: "recover:op-recover-1",
                IdempotencyKey: "honua-devops:recovery:op-recover-1",
                PolicyGate: "protected-recovery",
                AuthorizationDryRun: false,
                Actor: "operator@honua.io"),
            tenant: "tenant-a",
            priorRevision: "release/2026.02",
            candidateRevision: "release/2026.03",
            safetyPolicyDigest: "sha256:policy-digest",
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            operationId: "op-recover-1",
            out ActuationSpine.DeploymentRecoveryGrant? grant,
            out _));
        return grant!;
    }

    private static OperationRuntime ExecuteRuntime(bool protectedRecoveryEnabled)
        => OperationRuntime.SafeDefault with
        {
            ExecutionMode = ExecutionMode.Execute,
            ExecutionTier = ExecutionTier.ExecuteLowerEnv,
            DeployTargetId = "prod-api",
            ProtectedRecoveryEnabled = protectedRecoveryEnabled
        };

    private static BackendGateway CreateGateway(TestHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(BackendConfigurationFixture.Create(), httpClient);
    }

    private static OperatorPolicyModel DirectAllowedPolicy()
        => new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
}
