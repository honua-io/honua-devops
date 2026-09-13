using System.Net.Http;

using System.Net;
using System.Net.Http.Json;

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
    public async Task ExecuteRecoveryAsync_ValidGrant_ReportsServerRecoveryWithoutInventingGitConvergence()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/rollback", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(Operation("RolledBack"));
            }

            return TestHttpMessageHandler.JsonOk(Operation("Reconciling"));
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
        Assert.DoesNotContain(result.Findings, finding => finding.StartsWith("Restored desired revision:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, finding => finding.StartsWith("Quarantined rejected revision:", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Contains("no Git convergence receipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_WrongActor_RefusesAndCallsNoMutatingRoute()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(Operation("Reconciling")));
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
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(Operation("Reconciling", candidate: "release/2026.05")));
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
        // The second executor has a fresh spine with no in-memory claims. Only the
        // independently retained server operation prevents a second provider request.
        int rollbackCalls = 0;
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/rollback", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref rollbackCalls);
                return TestHttpMessageHandler.JsonOk(Operation("RolledBack"));
            }

            return TestHttpMessageHandler.JsonOk(Operation(rollbackCalls == 0 ? "Reconciling" : "RolledBack"));
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(protectedRecoveryEnabled: true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        RecoveryExecutor first = new(runtime, gateway, DirectAllowedPolicy(), spine);
        GitOpsExecutionResult firstResult = await first.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        ActuationSpine restartedSpine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor retry = new(runtime, gateway, DirectAllowedPolicy(), restartedSpine);
        GitOpsExecutionResult retryResult = await retry.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, firstResult.Status);
        Assert.True(firstResult.Mutated);
        Assert.Equal(GitOpsExecutionStatus.RolledBack, retryResult.Status);
        Assert.False(retryResult.Mutated);
        Assert.Equal(1, rollbackCalls);
    }

    // This fixture follows DeployOperationResponse/DeployProtectionResponse on server trunk.
    [Theory]
    [InlineData("RollbackRequested", GitOpsExecutionStatus.InProgress, RolloutJourneyStatus.Updating)]
    [InlineData("Reconciling", GitOpsExecutionStatus.InProgress, RolloutJourneyStatus.Updating)]
    [InlineData("Failed", GitOpsExecutionStatus.Failed, RolloutJourneyStatus.NeedsAttention)]
    [InlineData("ManualInterventionRequired", GitOpsExecutionStatus.Failed, RolloutJourneyStatus.NeedsAttention)]
    [InlineData("Succeeded", GitOpsExecutionStatus.Indeterminate, RolloutJourneyStatus.NeedsAttention)]
    [InlineData("future-status", GitOpsExecutionStatus.Indeterminate, RolloutJourneyStatus.NeedsAttention)]
    [InlineData(null, GitOpsExecutionStatus.Indeterminate, RolloutJourneyStatus.NeedsAttention)]
    [InlineData("RolledBack", GitOpsExecutionStatus.RolledBack, RolloutJourneyStatus.PreviousVersionRestored)]
    public async Task ExecuteRecoveryAsync_UsesServerOutcomeInsteadOfHttpSuccess(
        string? serverStatus, string expectedStatus, string expectedJourney)
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(
            Operation(request.Method == HttpMethod.Post ? serverStatus : "Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedJourney, RolloutJourneyStatus.From(result));
        Assert.True(result.Mutated);
        Assert.Equal(1, handler.CapturedRequests.Count(request => request.Method == "POST"));
        Assert.DoesNotContain(result.Findings, finding => finding.StartsWith("Restored desired revision:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, finding => finding.StartsWith("Quarantined rejected revision:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("operationId", "other-operation")]
    [InlineData("target.targetId", "other-target")]
    [InlineData("protection.phase", "expired")]
    [InlineData("protection.phase", "unavailable")]
    [InlineData("protection.policyDigest", "other-policy")]
    [InlineData("protection.previousRevision", "other-prior")]
    [InlineData("protection.candidateRevision", "other-candidate")]
    [InlineData("protection.observationDeadline", "2000-01-01T00:00:00Z")]
    [InlineData("protection.observationDeadline", "invalid")]
    [InlineData("protection", null)]
    public async Task ExecuteRecoveryAsync_RejectsMismatchedOrUnavailableServerScope(string field, string? value)
    {
        System.Text.Json.Nodes.JsonObject operation = System.Text.Json.JsonSerializer.SerializeToNode(Operation("Reconciling"))!.AsObject();
        string[] path = field.Split('.');
        if (path.Length == 1) operation[path[0]] = value;
        else operation[path[0]]![path[1]] = value;
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(operation));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        // BackendGateway rejects an operation-id substitution at the transport boundary.
        Assert.Equal(field == "operationId" ? GitOpsExecutionStatus.ContractUnavailable : GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Method == "POST");
    }

    [Theory]
    [InlineData("other-operation", "prod-api", "release/2026.03")]
    [InlineData("op-recover-1", "other-target", "release/2026.03")]
    [InlineData("op-recover-1", "prod-api", "release/2026.05")]
    public async Task ExecuteRecoveryAsync_RejectsSuccessfulResponseForDifferentScope(string id, string target, string candidate)
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(
            request.Method == HttpMethod.Post ? Operation("RolledBack", candidate, id, target) : Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Contains(id == "op-recover-1" ? "recovery-response-unverified" : "recovery-acknowledgement-unavailable", result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_LostAcknowledgementThenRestart_ObservesOneServerRecovery()
    {
        int providerMutations = 0;
        string durableStatus = "Reconciling";
        TestHttpMessageHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                providerMutations++;
                durableStatus = "RollbackRequested";
                throw new HttpRequestException("Connection reset after provider accepted recovery");
            }

            return TestHttpMessageHandler.JsonOk(Operation(durableStatus));
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine firstSpine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor first = new(runtime, gateway, DirectAllowedPolicy(), firstSpine);
        GitOpsExecutionResult firstResult = await first.ExecuteRecoveryAsync(
            IssueGrant(firstSpine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        // No shared grant or claim ledger: reconstruct the declared scope and consume the
        // surviving server operation, as the restarted caller must do.
        ActuationSpine restartedSpine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor restarted = new(runtime, gateway, DirectAllowedPolicy(), restartedSpine);
        GitOpsExecutionResult retryResult = await restarted.ExecuteRecoveryAsync(
            IssueGrant(restartedSpine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, firstResult.Status);
        Assert.Equal(GitOpsExecutionStatus.InProgress, retryResult.Status);
        Assert.False(retryResult.Mutated);
        Assert.Equal("op-recover-1", retryResult.OperationId);
        Assert.Equal("RollbackRequested", durableStatus);
        Assert.Equal(1, providerMutations);
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_AcknowledgedButUnverifiedResponse_PreservesMutationEvidence()
    {
        TestHttpMessageHandler handler = new(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") }
            : TestHttpMessageHandler.JsonOk(Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.True(result.Mutated);
        Assert.True(Assert.Single(result.BackendSteps!, step => step.Name == "deploy-operation-recover").MutatesState);
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_ForbiddenRollback_ReturnsApprovalRequired()
    {
        TestHttpMessageHandler handler = new(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "approval-required" }) }
            : TestHttpMessageHandler.JsonOk(Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Contains("recovery-refused", result.BlockingReasons);
    }

    [Theory]
    [InlineData("RolledBack", "recovering", "release/2026.01", "sha256:policy-digest", "release/2026.03", "release/2026.03")]
    [InlineData("RollbackRequested", "recovering", "release/2026.02", "sha256:other-policy", "release/2026.03", "release/2026.03")]
    [InlineData("Failed", "protected", "release/2026.02", "sha256:policy-digest", "release/2026.03", "release/2026.04")]
    public async Task ExecuteRecoveryAsync_ObservationStateWithMismatchedProtectionScope_IsRefused(
        string status, string phase, string prior, string policy, string candidate, string protectedCandidate)
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(
            Operation(status, candidate, phase: phase, policy: policy, prior: prior, protectedCandidate: protectedCandidate)));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Contains("recovery-protection-unavailable", result.BlockingReasons);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Method == "POST");
    }

    // Revisions and expected outcomes are declared independently of the executor.
    private static object Operation(string? status, string candidate = "release/2026.03",
        string operationId = "op-recover-1", string targetId = "prod-api",
        string phase = "observing", string policy = "sha256:policy-digest", string prior = "release/2026.02",
        string? protectedCandidate = null)
        => new
        {
            operationId,
            status,
            target = new { targetId, desiredRevision = candidate, currentRevision = prior },
            protection = new { phase, previousRevision = prior, candidateRevision = protectedCandidate ?? candidate, policyDigest = policy, observationDeadline = "2099-01-01T00:00:00Z" }
        };

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
