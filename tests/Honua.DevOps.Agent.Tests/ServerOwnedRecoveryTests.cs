using System.Net.Http;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// Server-owned recovery consumed as desired intent (issue #191). honua-server's reconciler fires
// its own rollback signal inside the post-activation observation window, and a settled RolledBack
// clears `protection` (DeployWorkflowReconciler/DeployWorkflowService call WithoutProtection).
// These fixtures follow that server trunk response shape. Revisions, versions and blockers are
// declared here, independently of the executor.
public sealed class ServerOwnedRecoveryTests : IDisposable
{
    private const string Target = "prod-api";
    private const string Prior = "release/2026.02";
    private const string Candidate = "release/2026.03";
    private const string Corrected = "release/2026.04";
    private const string Actor = "honua-devops:sync:prod-api";

    private readonly string _ledgerDirectory = Directory.CreateTempSubdirectory("honua-devops-server-recovery-").FullName;
    private readonly FileDesiredIntentLedger _ledger;

    public ServerOwnedRecoveryTests()
        => _ledger = new FileDesiredIntentLedger(Path.Combine(_ledgerDirectory, "audit.jsonl.desired-intent.jsonl"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_ledgerDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ServerRecoversDuringPoll_WithObservedPrior_RecordsRestoredIntentAndFencesNextReconcile()
    {
        int reads = 0;
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => ++reads == 1
            ? Observing("op-7f3", Candidate, Prior)
            : Settled("op-7f3", Candidate, "RolledBack"));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, result.Status);
        Assert.Empty(result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.PreviousVersionRestored, RolloutJourneyStatus.From(result));
        Assert.Equal(1, handler.CapturedRequests.Count(request => request.Uri.EndsWith("/submit", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.EndsWith("/rollback", StringComparison.Ordinal));

        DesiredIntentRecord restored = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Restored, Prior, "op-7f3"),
            (restored.Version, restored.Kind, restored.DesiredRevision, restored.OperationId));
        Assert.Equal([Candidate], restored.RejectedRevisions);
        // Direct policy combined with the control plane's own decision is receipted by operation.
        Assert.Equal("op-7f3", restored.ApprovalReference);
        Assert.Equal(Actor, restored.Actor);

        // The next reconcile of the rejected candidate issues nothing at all.
        TestHttpMessageHandler next = DeployHandler("op-again", Candidate, () => Settled("op-again", Candidate, "Succeeded"));
        using BackendGateway nextGateway = CreateGateway(next);
        GitOpsExecutionResult resurrect = await SyncAsync(Executor(nextGateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, resurrect.Status);
        Assert.Contains("desired-revision-quarantined", resurrect.BlockingReasons);
        Assert.Empty(next.CapturedRequests);
    }

    [Fact]
    public async Task ServerRecoversBeforeExposingPrior_QuarantinesWithoutClaimingRestoration()
    {
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => Settled("op-7f3", Candidate, "RolledBack"));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Equal(["restored-revision-unverified"], result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        Assert.True(result.Mutated);
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("restores `", StringComparison.Ordinal));

        DesiredIntentRecord rejected = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Rejected, string.Empty), (rejected.Version, rejected.Kind, rejected.DesiredRevision));
        Assert.Equal([Candidate], rejected.RejectedRevisions);

        TestHttpMessageHandler next = DeployHandler("op-again", Candidate, () => Settled("op-again", Candidate, "Succeeded"));
        using BackendGateway nextGateway = CreateGateway(next);
        GitOpsExecutionResult resurrect = await SyncAsync(Executor(nextGateway), Candidate);
        Assert.Contains("desired-revision-quarantined", resurrect.BlockingReasons);
        Assert.Empty(next.CapturedRequests);
    }

    [Fact]
    public async Task RecoverySettledWhileUnwatched_RestartFoldsItOnceAndRefusesResurrection()
    {
        // The previous process recorded the approval and died mid-poll; the server then recovered.
        await CommitAsync(0, Approved(Candidate, "op-7f3"));
        TestHttpMessageHandler restartHandler = new(request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/op-7f3", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(Settled("op-7f3", Candidate, "RolledBack"))
                : TestHttpMessageHandler.JsonOk(new { status = "unexpected" }));
        using BackendGateway restartGateway = CreateGateway(restartHandler);

        GitOpsExecutionResult first = await SyncAsync(Executor(restartGateway), Candidate);
        GitOpsExecutionResult second = await SyncAsync(Executor(restartGateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, first.Status);
        Assert.Contains("desired-revision-quarantined", first.BlockingReasons);
        Assert.False(first.Mutated);
        Assert.Contains("desired-revision-quarantined", second.BlockingReasons);
        CapturedRequest read = Assert.Single(restartHandler.CapturedRequests);
        Assert.Equal("GET", read.Method);

        DesiredIntentRecord rejected = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Rejected, "op-7f3"), (rejected.Version, rejected.Kind, rejected.OperationId));
        Assert.Equal([Candidate], rejected.RejectedRevisions);

        // A corrected forward revision proceeds and carries the quarantine forward.
        TestHttpMessageHandler forwardHandler = DeployHandler("op-fix-4", Corrected, () => Settled("op-fix-4", Corrected, "Succeeded"));
        using BackendGateway forwardGateway = CreateGateway(forwardHandler);
        GitOpsExecutionResult forward = await SyncAsync(Executor(forwardGateway), Corrected);

        Assert.Equal(GitOpsExecutionStatus.Succeeded, forward.Status);
        Assert.Equal(RolloutJourneyStatus.UpdateComplete, RolloutJourneyStatus.From(forward));
        DesiredIntentRecord approved = await LatestAsync();
        Assert.Equal((3L, DesiredIntentKind.Approved, Corrected), (approved.Version, approved.Kind, approved.DesiredRevision));
        Assert.Equal([Candidate], approved.RejectedRevisions);
    }

    [Fact]
    public async Task NewerIntentRecordedDuringPoll_ConflictsAndIsNotOverwritten()
    {
        int reads = 0;
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () =>
        {
            if (++reads == 1)
            {
                return Observing("op-7f3", Candidate, Prior);
            }

            // Another approved change lands after this sync recorded its approval.
            Assert.Equal(DesiredIntentCommitStatus.Committed,
                _ledger.TryCommitAsync(1, Approved(Corrected, "op-newer"), CancellationToken.None).GetAwaiter().GetResult().Status);
            return Settled("op-7f3", Candidate, "RolledBack");
        });
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Equal(["desired-intent-conflict"], result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Approved, Corrected), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
    }

    [Theory]
    [InlineData("prod-api", "release/2026.05")]
    [InlineData("other-target", Candidate)]
    public async Task ServerRecoveryForDifferentScope_IsIndeterminateAndLeavesIntent(string targetId, string candidate)
    {
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => Settled("op-7f3", candidate, "RolledBack", targetId));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Equal(["server-recovery-unverified"], result.BlockingReasons);
        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((1L, DesiredIntentKind.Approved, Candidate), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
    }

    [Fact]
    public async Task ObservationWindowOpenAtPollBudget_ReportsConfirmingServiceHealth()
    {
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => Observing("op-7f3", Candidate, Prior));
        using BackendGateway gateway = CreateGateway(handler);
        SteppingTimeProvider clock = new();
        GitOpsExecutor executor = new(
            ExecuteRuntime(), gateway, DirectAllowedPolicy(),
            pollPolicy: new DeployPollPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            delay: (interval, _) => { clock.Advance(interval); return Task.CompletedTask; },
            timeProvider: clock,
            intentLedger: _ledger);

        GitOpsExecutionResult result = await SyncAsync(executor, Candidate);

        Assert.Equal(GitOpsExecutionStatus.InProgress, result.Status);
        Assert.Equal(RolloutJourneyStatus.ConfirmingServiceHealth, RolloutJourneyStatus.From(result));
        Assert.Equal("Confirming service health", RolloutJourneyStatus.Label(RolloutJourneyStatus.From(result)));
        Assert.Equal((1L, DesiredIntentKind.Approved), ((await LatestAsync()).Version, (await LatestAsync()).Kind));
    }

    [Fact]
    public async Task RecoveryExecutorRestart_AfterServerSettledAndClearedProtection_RecordsRestorationOnce()
    {
        await CommitAsync(0, Approved(Candidate, "op-recover-1") with { Actor = "operator@honua.io" });
        int providerMutations = 0;
        bool settled = false;
        TestHttpMessageHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                providerMutations++;
                settled = true;
                return TestHttpMessageHandler.JsonOk(Settled("op-recover-1", Candidate, "RolledBack"));
            }

            return TestHttpMessageHandler.JsonOk(settled
                ? Settled("op-recover-1", Candidate, "RolledBack")
                : Observing("op-recover-1", Candidate, Prior));
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime();

        ActuationSpine firstSpine = new(runtime, DirectAllowedPolicy());
        GitOpsExecutionResult failed = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), firstSpine, new FailingCommitLedger(_ledger))
            .ExecuteRecoveryAsync(IssueGrant(firstSpine), "operator@honua.io", Target, "health-regression", CancellationToken.None);
        ActuationSpine restartedSpine = new(runtime, DirectAllowedPolicy());
        GitOpsExecutionResult restarted = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), restartedSpine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(restartedSpine), "operator@honua.io", Target, "health-regression", CancellationToken.None);

        Assert.Equal(["desired-intent-write-failed"], failed.BlockingReasons);
        Assert.Equal(GitOpsExecutionStatus.RolledBack, restarted.Status);
        Assert.False(restarted.Mutated);
        Assert.Equal(1, providerMutations);
        DesiredIntentRecord restored = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Restored, Prior), (restored.Version, restored.Kind, restored.DesiredRevision));
        Assert.Equal([Candidate], restored.RejectedRevisions);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("RollbackRequested")]
    [InlineData("ManualInterventionRequired")]
    public async Task RecoveryExecutor_UnsettledObservationWithoutProtection_IsStillRefused(string status)
    {
        await CommitAsync(0, Approved(Candidate, "op-recover-1"));
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(Settled("op-recover-1", Candidate, status)));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime();
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());

        GitOpsExecutionResult result = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(spine), "operator@honua.io", Target, "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.Contains("recovery-protection-unavailable", result.BlockingReasons);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Method == "POST");
        Assert.Equal((1L, DesiredIntentKind.Approved), ((await LatestAsync()).Version, (await LatestAsync()).Kind));
    }

    // ---- server response shapes (DeployOperationResponse on server trunk) ----

    private static object Observing(string operationId, string candidate, string prior)
        => new
        {
            operationId,
            status = "Reconciling",
            currentPhase = $"Observing candidate '{candidate}' (540s remaining before the deploy is fully committed).",
            target = new { targetId = Target, desiredRevision = candidate, currentRevision = prior },
            protection = new
            {
                phase = "observing",
                previousRevision = prior,
                candidateRevision = candidate,
                policyDigest = "sha256:policy-digest",
                observationDeadline = "2099-01-01T00:00:00Z"
            }
        };

    private static object Settled(string operationId, string candidate, string status, string targetId = Target)
        => new
        {
            operationId,
            status,
            currentPhase = "Deploy backend recommended rollback.",
            target = new { targetId, desiredRevision = candidate, currentRevision = candidate },
            protection = (object?)null,
            actuatorReceipt = new { receiptId = $"rcpt-{operationId}", operationId }
        };

    private static TestHttpMessageHandler DeployHandler(string operationId, string revision, Func<object> read)
        => new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    operationId,
                    status = "Planned",
                    target = new { targetId = Target, desiredRevision = revision }
                });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    operationId,
                    status = "Submitted",
                    target = new { targetId = Target, desiredRevision = revision }
                });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith($"/{operationId}", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(read());
            }

            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });

    // ---- helpers ----

    private GitOpsExecutor Executor(BackendGateway gateway)
        => new(
            ExecuteRuntime(), gateway, DirectAllowedPolicy(),
            pollPolicy: new DeployPollPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)),
            delay: (_, _) => Task.CompletedTask,
            intentLedger: _ledger);

    private static Task<GitOpsExecutionResult> SyncAsync(GitOpsExecutor executor, string revision)
        => executor.ExecuteSyncAsync(
            desiredRevision: revision,
            currentRevision: null,
            reason: "ship prod-api",
            idempotencyKey: $"honua-devops:proposal:prod-api:{revision}:sync:{Guid.NewGuid():N}",
            correlationId: Actor,
            priority: "normal",
            parameters: new Dictionary<string, string> { ["service"] = "prod-api", ["environments"] = "dev", ["action"] = "sync" },
            authorizationDryRun: false,
            policyGate: "lower-env-execution",
            CancellationToken.None);

    private async Task<DesiredIntentRecord> LatestAsync()
        => (await _ledger.ReadLatestAsync(Target, CancellationToken.None)).Latest!;

    private async Task CommitAsync(long expectedVersion, DesiredIntentRecord record)
        => Assert.Equal(DesiredIntentCommitStatus.Committed,
            (await _ledger.TryCommitAsync(expectedVersion, record, CancellationToken.None)).Status);

    private static DesiredIntentRecord Approved(string revision, string operationId)
        => new(Target, 0, DesiredIntentKind.Approved, revision, [], operationId,
            $"approval-receipt-{operationId}", Actor, null, DateTimeOffset.UtcNow);

    private static ActuationSpine.DeploymentRecoveryGrant IssueGrant(ActuationSpine spine)
    {
        Assert.True(spine.AuthorizeRecoveryGrant(
            new ActuationRequest(
                ActuatorId: "honua.deploy-operation.recovery",
                Action: "recover",
                Target: Target,
                Environments: ["prod"],
                DesiredState: "recover:op-recover-1",
                IdempotencyKey: "honua-devops:recovery:op-recover-1",
                PolicyGate: "protected-recovery",
                AuthorizationDryRun: false,
                Actor: "operator@honua.io"),
            tenant: "tenant-a",
            priorRevision: Prior,
            candidateRevision: Candidate,
            safetyPolicyDigest: "sha256:policy-digest",
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            operationId: "op-recover-1",
            out ActuationSpine.DeploymentRecoveryGrant? grant,
            out _));
        return grant!;
    }

    private static OperationRuntime ExecuteRuntime()
        => OperationRuntime.SafeDefault with
        {
            ExecutionMode = ExecutionMode.Execute,
            ExecutionTier = ExecutionTier.ExecuteLowerEnv,
            DeployTargetId = Target,
            ProtectedRecoveryEnabled = true
        };

    private static BackendGateway CreateGateway(TestHttpMessageHandler handler)
        => new(BackendConfigurationFixture.Create(), new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    private static OperatorPolicyModel DirectAllowedPolicy()
        => new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);

    private sealed class FailingCommitLedger(IDesiredIntentLedger inner) : IDesiredIntentLedger
    {
        public Task<DesiredIntentSnapshot> ReadLatestAsync(string target, CancellationToken cancellationToken)
            => inner.ReadLatestAsync(target, cancellationToken);

        public Task<DesiredIntentCommitResult> TryCommitAsync(long expectedVersion, DesiredIntentRecord next, CancellationToken cancellationToken)
            => Task.FromResult(new DesiredIntentCommitResult(DesiredIntentCommitStatus.WriteFailed, null, "injected: no space left on device"));
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
