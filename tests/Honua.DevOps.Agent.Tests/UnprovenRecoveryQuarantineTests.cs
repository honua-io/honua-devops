using System.Net.Http;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// Recovery the server TRIGGERED and could not PROVE (issue #191; honua-release#321 box 3:
// controller crash, failed rollback, concurrent service changes, missing/stale telemetry).
//
// This is not a RolledBack. honua-server's DeployWorkflowReconciler settles the operation
// `ManualInterventionRequired` and RETAINS the protection window as phase `unavailable` with
// reason `rollback-failed` (terminal provider refusal) or `rollback-retry-budget-exhausted`
// (bounded retries never settled). That retained record is the only durable evidence the
// candidate was rejected.
//
// The expected outcomes below are derived from that server contract, not from a snapshot of
// the executor: a failed recovery restores NOTHING, so no restoration may be claimed even
// though the payload still exposes `protection.previousRevision`; and the rejected candidate
// must be quarantined anyway, or the next reconcile converges straight back onto the revision
// the safety policy just rejected.
//
// The `complete-protection-failed` fixture is the deliberate counter-case: it shares the
// `unavailable` phase but the candidate PASSED its window, so quarantining there would fence a
// revision the server never rejected.
public sealed class UnprovenRecoveryQuarantineTests : IDisposable
{
    private const string Target = "prod-api";
    private const string Prior = "release/2026.02";
    private const string Candidate = "release/2026.03";
    private const string Corrected = "release/2026.04";
    private const string Actor = "honua-devops:sync:prod-api";

    private readonly string _ledgerDirectory = Directory.CreateTempSubdirectory("honua-devops-unproven-recovery-").FullName;
    private readonly FileDesiredIntentLedger _ledger;

    public UnprovenRecoveryQuarantineTests()
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

    [Theory]
    [InlineData(DeployProtectionReasonCodes.RollbackFailed)]
    [InlineData(DeployProtectionReasonCodes.RollbackRetryBudgetExhausted)]
    public async Task RecoveryTriggeredAndNotProven_QuarantinesCandidateWithoutClaimingRestoration(string reasonCode)
    {
        int reads = 0;
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => ++reads == 1
            ? Observing("op-7f3", Candidate, Prior)
            : RecoveryUnavailable("op-7f3", Candidate, Prior, reasonCode));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        // Fail-closed: the deploy is not Succeeded, and it is not a restoration.
        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Equal(["protected-recovery-unproven"], result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        Assert.Equal("Needs attention", RolloutJourneyStatus.Label(RolloutJourneyStatus.From(result)));
        Assert.True(result.Mutated);
        Assert.Equal(DeployProtectionPhases.Unavailable, result.ProtectionPhase);
        Assert.Equal(reasonCode, result.ProtectionReasonCode);

        // The payload DOES expose the prior revision, and it must still not be claimed as
        // restored: the rollback that would have restored it is exactly what failed.
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("restores `", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding =>
            finding.Contains("could NOT be restored automatically", StringComparison.Ordinal));

        DesiredIntentRecord rejected = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Rejected, string.Empty, "op-7f3"),
            (rejected.Version, rejected.Kind, rejected.DesiredRevision, rejected.OperationId));
        Assert.Equal([Candidate], rejected.RejectedRevisions);
        // Under direct policy the control plane's own decision is receipted by operation.
        Assert.Equal("op-7f3", rejected.ApprovalReference);

        // The next reconcile of the rejected candidate issues nothing at all.
        TestHttpMessageHandler next = DeployHandler("op-again", Candidate, () => Observing("op-again", Candidate, Prior));
        using BackendGateway nextGateway = CreateGateway(next);
        GitOpsExecutionResult resurrect = await SyncAsync(Executor(nextGateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, resurrect.Status);
        Assert.Contains("desired-revision-quarantined", resurrect.BlockingReasons);
        Assert.Empty(next.CapturedRequests);

        // A corrected forward revision proceeds and carries the quarantine forward.
        TestHttpMessageHandler forward = DeployHandler("op-fix-4", Corrected, () => Settled("op-fix-4", Corrected, "Succeeded"));
        using BackendGateway forwardGateway = CreateGateway(forward);
        GitOpsExecutionResult fixedForward = await SyncAsync(Executor(forwardGateway), Corrected);

        Assert.Equal(GitOpsExecutionStatus.Succeeded, fixedForward.Status);
        DesiredIntentRecord approved = await LatestAsync();
        Assert.Equal((3L, DesiredIntentKind.Approved, Corrected), (approved.Version, approved.Kind, approved.DesiredRevision));
        Assert.Equal([Candidate], approved.RejectedRevisions);
    }

    [Fact]
    public async Task RecoveryNotProvenWhileUnwatched_IsFoldedOnRestartBeforeAnyMutation()
    {
        // The previous process recorded the approval and died mid-poll; the server then tried to
        // recover the candidate and could not complete it.
        await CommitAsync(0, Approved(Candidate, "op-7f3"));
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/op-7f3", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(RecoveryUnavailable("op-7f3", Candidate, Prior, DeployProtectionReasonCodes.RollbackFailed))
                : TestHttpMessageHandler.JsonOk(new { status = "unexpected" }));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult first = await SyncAsync(Executor(gateway), Candidate);
        GitOpsExecutionResult second = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, first.Status);
        Assert.Contains("desired-revision-quarantined", first.BlockingReasons);
        Assert.False(first.Mutated);
        Assert.Contains("desired-revision-quarantined", second.BlockingReasons);

        // One read folded it in; the second reconcile needed no server call at all.
        CapturedRequest read = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", read.Method);

        DesiredIntentRecord rejected = await LatestAsync();
        Assert.Equal((2L, DesiredIntentKind.Rejected, "op-7f3"), (rejected.Version, rejected.Kind, rejected.OperationId));
        Assert.Equal([Candidate], rejected.RejectedRevisions);
    }

    [Fact]
    public async Task ObservationWindowCompletedButPriorReplicaNotRetired_QuarantinesNothing()
    {
        // `complete-protection-failed` shares phase `unavailable`, but the candidate PASSED its
        // observation window. Nothing rejected it, so nothing may fence it.
        int reads = 0;
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => ++reads == 1
            ? Observing("op-7f3", Candidate, Prior)
            : RecoveryUnavailable("op-7f3", Candidate, Prior, DeployProtectionReasonCodes.CompleteProtectionFailed));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.Failed, result.Status);
        Assert.Empty(result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        Assert.Contains(result.Findings, finding => finding.Contains("no revision was rejected", StringComparison.Ordinal));

        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((1L, DesiredIntentKind.Approved, Candidate), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);

        // Re-deploying that same revision is NOT refused: the server never rejected it.
        TestHttpMessageHandler retry = DeployHandler("op-retry", Candidate, () => Settled("op-retry", Candidate, "Succeeded"));
        using BackendGateway retryGateway = CreateGateway(retry);
        GitOpsExecutionResult again = await SyncAsync(Executor(retryGateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.Succeeded, again.Status);
        Assert.DoesNotContain("desired-revision-quarantined", again.BlockingReasons);
    }

    [Fact]
    public async Task RecoveryStillExecutingAtPollBudget_ReportsUpdatingAndRecordsNothing()
    {
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () =>
            Recovering("op-7f3", Candidate, Prior, DeployProtectionReasonCodes.RollbackRetryPending));
        using BackendGateway gateway = CreateGateway(handler);
        SteppingTimeProvider clock = new();
        GitOpsExecutor executor = new(
            ExecuteRuntime(), gateway, DirectAllowedPolicy(),
            pollPolicy: new DeployPollPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            delay: (interval, _) => { clock.Advance(interval); return Task.CompletedTask; },
            timeProvider: clock,
            intentLedger: _ledger);

        GitOpsExecutionResult result = await SyncAsync(executor, Candidate);

        // A recovery in flight is still an update in flight, never "Confirming service health"
        // (the candidate's health has already been decided) and never a terminal verdict.
        Assert.Equal(GitOpsExecutionStatus.InProgress, result.Status);
        Assert.Equal(RolloutJourneyStatus.Updating, RolloutJourneyStatus.From(result));
        Assert.Contains(result.Findings, finding =>
            finding.Contains("is being retried", StringComparison.Ordinal));

        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((1L, DesiredIntentKind.Approved, Candidate), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
    }

    [Theory]
    [InlineData("other-target", Candidate)]
    [InlineData(Target, "release/2026.09")]
    public async Task UnprovenRecoveryForDifferentScope_RecordsNothing(string targetId, string candidate)
    {
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () =>
            RecoveryUnavailable("op-7f3", candidate, Prior, DeployProtectionReasonCodes.RollbackFailed, targetId));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Equal(["server-recovery-unverified"], result.BlockingReasons);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("reports that recovery could not be completed", StringComparison.Ordinal));

        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((1L, DesiredIntentKind.Approved, Candidate), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
    }

    [Fact]
    public async Task UnprovenRecoveryQuarantineCannotBeWritten_IsExplicitAndLeavesTheCandidateUnfenced()
    {
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () =>
            RecoveryUnavailable("op-7f3", Candidate, Prior, DeployProtectionReasonCodes.RollbackFailed));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            ExecuteRuntime(), gateway, DirectAllowedPolicy(),
            pollPolicy: new DeployPollPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)),
            delay: (_, _) => Task.CompletedTask,
            intentLedger: new RejectWriteLedger(_ledger));

        GitOpsExecutionResult result = await SyncAsync(executor, Candidate);

        // The quarantine is not durable, so the failure is reported rather than implied by a
        // later refusal that would not happen.
        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Equal(["desired-intent-write-failed"], result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        Assert.Contains(result.Findings, finding =>
            finding.Contains("candidate quarantine could not be recorded", StringComparison.Ordinal));

        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((1L, DesiredIntentKind.Approved, Candidate), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
    }

    [Fact]
    public async Task ProtectionPhaseAndReasonCode_AreCarriedAsAPair_NotIndependently()
    {
        // Poll 1 carries a reason code with an `observing` phase; poll 2 moves the window to
        // `unavailable` and reports no reason code. Retaining the two fields independently would
        // pair the stale `telemetry-evidence-pending` with the fresh `unavailable` phase.
        int reads = 0;
        TestHttpMessageHandler handler = DeployHandler("op-7f3", Candidate, () => ++reads == 1
            ? ObservingWithReason("op-7f3", Candidate, Prior, DeployProtectionReasonCodes.TelemetryEvidencePending)
            : UnavailableWithoutReason("op-7f3", Candidate, Prior));
        using BackendGateway gateway = CreateGateway(handler);

        GitOpsExecutionResult result = await SyncAsync(Executor(gateway), Candidate);

        Assert.Equal(DeployProtectionPhases.Unavailable, result.ProtectionPhase);
        Assert.Null(result.ProtectionReasonCode);

        // Without a rejection reason code nothing is quarantined: the server has not said the
        // candidate was rejected.
        DesiredIntentRecord latest = await LatestAsync();
        Assert.Equal((1L, DesiredIntentKind.Approved, Candidate), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
        Assert.DoesNotContain(result.Findings, finding =>
            finding.Contains("health evidence has not arrived", StringComparison.Ordinal));
    }

    // ---- journey projection: unit-level, independent of any executor run ----

    [Theory]
    [InlineData(DeployProtectionReasonCodes.RollbackFailed)]
    [InlineData(DeployProtectionReasonCodes.RollbackRetryBudgetExhausted)]
    public void UnprovenRecovery_OutranksEveryOtherJourneySignal(string reasonCode)
    {
        foreach (string executionStatus in new[]
                 {
                     GitOpsExecutionStatus.RolledBack,
                     GitOpsExecutionStatus.Succeeded,
                     GitOpsExecutionStatus.InProgress,
                     GitOpsExecutionStatus.Failed
                 })
        {
            Assert.Equal(
                RolloutJourneyStatus.NeedsAttention,
                RolloutJourneyStatus.Describe(
                    executionStatus,
                    serverStatus: "ManualInterventionRequired",
                    currentPhase: "Observing candidate; confirming health.",
                    DeployProtectionPhases.Unavailable,
                    reasonCode));
        }
    }

    [Theory]
    [InlineData(DeployProtectionPhases.Observing, RolloutJourneyStatus.ConfirmingServiceHealth)]
    [InlineData(DeployProtectionPhases.Protected, RolloutJourneyStatus.ConfirmingServiceHealth)]
    [InlineData(DeployProtectionPhases.Recovering, RolloutJourneyStatus.Updating)]
    [InlineData(DeployProtectionPhases.Unavailable, RolloutJourneyStatus.NeedsAttention)]
    public void StructuredProtectionPhase_DrivesTheJourneyWithoutFreeTextPhrasing(string phase, string expected)
        => Assert.Equal(
            expected,
            RolloutJourneyStatus.Describe(
                GitOpsExecutionStatus.InProgress,
                serverStatus: "Reconciling",
                // Deliberately free of every legacy token ("smoke"/"verify"/"observ"/"health"/
                // "confirm"): the structured phase alone has to carry the journey.
                currentPhase: "Backend is working on it.",
                phase,
                protectionReasonCode: null));

    [Fact]
    public void ExpiredProtectionWindow_DefersToTheOperationOutcome()
    {
        Assert.Equal(
            RolloutJourneyStatus.UpdateComplete,
            RolloutJourneyStatus.Describe(
                GitOpsExecutionStatus.Succeeded, "Succeeded", "Post-activation observation window elapsed.",
                DeployProtectionPhases.Expired, DeployProtectionReasonCodes.ObservationWindowElapsed));
        Assert.Equal(
            RolloutJourneyStatus.Updating,
            RolloutJourneyStatus.Describe(
                GitOpsExecutionStatus.InProgress, "Reconciling", "Applying revision.",
                DeployProtectionPhases.Expired, DeployProtectionReasonCodes.ObservationWindowElapsed));
    }

    [Fact]
    public void NoProtectionWindow_KeepsTheLegacyFreeTextProjection()
    {
        Assert.Equal(
            RolloutJourneyStatus.ConfirmingServiceHealth,
            RolloutJourneyStatus.Describe(
                GitOpsExecutionStatus.InProgress, "Reconciling", "Running post-deploy smoke checks.",
                protectionPhase: null, protectionReasonCode: null));
        Assert.Equal(
            RolloutJourneyStatus.Updating,
            RolloutJourneyStatus.Describe(
                GitOpsExecutionStatus.InProgress, "Reconciling", "Applying revision.",
                protectionPhase: null, protectionReasonCode: null));
    }

    // ---- plain-language fault classes (honua-release#321 box 3) ----

    [Theory]
    [InlineData(DeployProtectionReasonCodes.TelemetryEvidencePending, "health evidence has not arrived")]
    [InlineData(DeployProtectionReasonCodes.ObservationWindowElapsed, "passed its post-activation health window")]
    [InlineData(DeployProtectionReasonCodes.RollbackRequested, "previous version is being restored")]
    [InlineData(DeployProtectionReasonCodes.RollbackRetryPending, "is being retried")]
    [InlineData(DeployProtectionReasonCodes.RollbackFailed, "could NOT be restored automatically")]
    [InlineData(DeployProtectionReasonCodes.RollbackRetryBudgetExhausted, "could NOT be restored automatically")]
    [InlineData(DeployProtectionReasonCodes.CompleteProtectionFailed, "no revision was rejected")]
    public void EveryServerReasonCode_HasPlainLanguageWithoutTelemetryOrGitMechanics(string reasonCode, string expected)
    {
        string described = Assert.IsType<string>(
            DeployProtectionReasonCodes.Describe(DeployProtectionPhases.Unavailable, reasonCode));
        Assert.Contains(expected, described, StringComparison.Ordinal);

        // "No required Git/PR/telemetry mechanics in ordinary agent interaction": the sentence an
        // operator reads must never name the plumbing that produced it.
        foreach (string mechanic in new[]
                 {
                     "telemetry", "Git", "git ", "commit sha", "branch", "pull request", "PR ",
                     "PromQL", "Prometheus", "metric", "query", "reconcile", "manifest", "digest"
                 })
        {
            Assert.DoesNotContain(mechanic, described, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UnrecognizedProtectionState_SaysNothingRatherThanGuessing()
    {
        Assert.Null(DeployProtectionReasonCodes.Describe(DeployProtectionPhases.Expired, reasonCode: null));
        Assert.Null(DeployProtectionReasonCodes.Describe(phase: null, reasonCode: null));
        Assert.Null(DeployProtectionReasonCodes.Describe("something-new", "a-code-this-build-does-not-know"));
    }

    [Fact]
    public void OnlyRejectionReasonCodes_CountAsAnUnprovenRecovery()
    {
        Assert.True(DeployProtectionReasonCodes.IsUnprovenRecovery(DeployProtectionReasonCodes.RollbackFailed));
        Assert.True(DeployProtectionReasonCodes.IsUnprovenRecovery(DeployProtectionReasonCodes.RollbackRetryBudgetExhausted));
        Assert.False(DeployProtectionReasonCodes.IsUnprovenRecovery(DeployProtectionReasonCodes.CompleteProtectionFailed));
        Assert.False(DeployProtectionReasonCodes.IsUnprovenRecovery(DeployProtectionReasonCodes.RollbackRetryPending));
        Assert.False(DeployProtectionReasonCodes.IsUnprovenRecovery(DeployProtectionReasonCodes.RollbackRequested));
        Assert.False(DeployProtectionReasonCodes.IsUnprovenRecovery(null));
    }

    // ---- server response shapes (DeployOperationResponse on honua-server 8862065) ----

    private static object Observing(string operationId, string candidate, string prior)
        => new
        {
            operationId,
            status = "Reconciling",
            currentPhase = $"Observing candidate '{candidate}' (540s remaining before the deploy is fully committed).",
            target = new { targetId = Target, desiredRevision = candidate, currentRevision = prior },
            protection = new
            {
                phase = DeployProtectionPhases.Observing,
                previousRevision = prior,
                candidateRevision = candidate,
                policyDigest = "sha256:policy-digest",
                observationDeadline = "2099-01-01T00:00:00Z"
            }
        };

    // DeployWorkflowReconciler keeps the operation Reconciling with the window in `recovering`
    // while a triggered rollback is in flight or retrying.
    private static object Recovering(string operationId, string candidate, string prior, string reasonCode)
        => new
        {
            operationId,
            status = "Reconciling",
            currentPhase = "Automatic rollback did not take on attempt 1 of 3 and will be retried.",
            target = new { targetId = Target, desiredRevision = candidate, currentRevision = prior },
            protection = new
            {
                phase = DeployProtectionPhases.Recovering,
                previousRevision = prior,
                candidateRevision = candidate,
                policyDigest = "sha256:policy-digest",
                observationDeadline = "2099-01-01T00:00:00Z",
                recoveryDeadline = "2099-01-01T00:10:00Z",
                reasonCode
            }
        };

    // DeployWorkflowReconciler settles ManualInterventionRequired and RETAINS the protection
    // record as `unavailable` when recovery itself cannot be proven, so the durable record keeps
    // the evidence. `previousRevision` is still present and was NOT restored.
    private static object RecoveryUnavailable(
        string operationId, string candidate, string prior, string reasonCode, string targetId = Target)
        => new
        {
            operationId,
            status = "ManualInterventionRequired",
            blockingReasons = Array.Empty<string>(),
            currentPhase = "Automatic rollback could not be completed and requires manual intervention.",
            errorMessage = "Automatic rollback failed: deploy controller unreachable.",
            target = new { targetId, desiredRevision = candidate, currentRevision = candidate },
            protection = new
            {
                phase = DeployProtectionPhases.Unavailable,
                previousRevision = prior,
                candidateRevision = candidate,
                policyDigest = "sha256:policy-digest",
                observationDeadline = "2099-01-01T00:00:00Z",
                recoveryDeadline = "2099-01-01T00:10:00Z",
                reasonCode
            },
            actuatorReceipt = new { receiptId = $"rcpt-{operationId}", operationId }
        };

    private static object ObservingWithReason(string operationId, string candidate, string prior, string reasonCode)
        => new
        {
            operationId,
            status = "Reconciling",
            currentPhase = "Observation window elapsed, but its health gate has not passed.",
            target = new { targetId = Target, desiredRevision = candidate, currentRevision = prior },
            protection = new
            {
                phase = DeployProtectionPhases.Observing,
                previousRevision = prior,
                candidateRevision = candidate,
                policyDigest = "sha256:policy-digest",
                observationDeadline = "2099-01-01T00:00:00Z",
                reasonCode
            }
        };

    private static object UnavailableWithoutReason(string operationId, string candidate, string prior)
        => new
        {
            operationId,
            status = "ManualInterventionRequired",
            blockingReasons = Array.Empty<string>(),
            currentPhase = "Requires manual intervention.",
            target = new { targetId = Target, desiredRevision = candidate, currentRevision = candidate },
            protection = new
            {
                phase = DeployProtectionPhases.Unavailable,
                previousRevision = prior,
                candidateRevision = candidate,
                policyDigest = "sha256:policy-digest",
                observationDeadline = "2099-01-01T00:00:00Z"
            },
            actuatorReceipt = new { receiptId = $"rcpt-{operationId}", operationId }
        };

    private static object Settled(string operationId, string candidate, string status)
        => new
        {
            operationId,
            status,
            blockingReasons = Array.Empty<string>(),
            currentPhase = "Deploy settled.",
            target = new { targetId = Target, desiredRevision = candidate, currentRevision = candidate },
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

    // The approval commits; only the recovery record's write fails.
    private sealed class RejectWriteLedger(IDesiredIntentLedger inner) : IDesiredIntentLedger
    {
        public Task<DesiredIntentSnapshot> ReadLatestAsync(string target, CancellationToken cancellationToken)
            => inner.ReadLatestAsync(target, cancellationToken);

        public Task<DesiredIntentCommitResult> TryCommitAsync(
            long expectedVersion, DesiredIntentRecord next, CancellationToken cancellationToken)
            => next.Kind is DesiredIntentKind.Rejected or DesiredIntentKind.Restored
                ? Task.FromResult(new DesiredIntentCommitResult(
                    DesiredIntentCommitStatus.WriteFailed, null, "injected: no space left on device"))
                : inner.TryCommitAsync(expectedVersion, next, cancellationToken);
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
