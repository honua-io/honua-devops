using System.Net;
using System.Net.Http;
using System.Text.Json;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.DesiredState;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// Proves the non-negotiable safety invariants of the GitOps actuation spine
// (GitOpsExecutor / PromotionExecutor / RollbackExecutor):
//   1. plan-mode = zero mutation, zero backend calls.
//   2. execute + unapproved (pr-first / server AwaitingApproval) = create but NO submit.
//   3. execute + approved (direct-allowed) = create + submit + poll to terminal.
//   4. data-affecting rollback without approval = refused (never auto-issued).
//   5. (issue #153) the desired-state apply happens ONLY after the durable operation exists
//      and its approval gate is satisfied — never on the planning path, and never at all
//      when the operation is parked for approval.
public class GitOpsExecutorTests
{
    private static readonly IReadOnlyDictionary<string, string> SyncParameters = new Dictionary<string, string>
    {
        ["service"] = "roads-api",
        ["environments"] = "dev",
        ["action"] = "sync"
    };

    // ---- Invariant 1: plan mode mutates nothing and makes no backend calls. ----

    [Fact]
    public async Task ExecuteSyncAsync_PlanMode_DoesNotMutateOrCallBackend()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Plan, deployTargetId: "prod-api"),
            gateway,
            OperatorPolicyModel.Default);

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.PlanOnly, result.Status);
        Assert.False(result.Mutated);
        Assert.Null(result.OperationId);
        // SAFETY: nothing was sent to the backend at all in the default plan posture.
        Assert.Empty(handler.CapturedRequests);
        Assert.DoesNotContain(result.BackendSteps, step => step.MutatesState);
    }

    [Fact]
    public async Task ExecuteSyncAsync_AuthorizationDryRun_StaysPlanOnlyEvenInExecuteMode()
    {
        // Execute mode but the per-request authorization resolved to dry-run (e.g. plan/observe
        // tier). The master safety switch still forbids mutation.
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor, authorizationDryRun: true);

        Assert.Equal(GitOpsExecutionStatus.PlanOnly, result.Status);
        Assert.False(result.Mutated);
        Assert.Empty(handler.CapturedRequests);
    }

    // ---- Invariant 2: execute + unapproved creates the op but never submits. ----

    [Fact]
    public async Task ExecuteSyncAsync_ExecutePrFirst_CreatesButDoesNotSubmit()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "AwaitingApproval" })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            OperatorPolicyModel.Default); // pr-first

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.AwaitingApproval, result.Status);
        Assert.Equal("op-7f3", result.OperationId);
        // The durable operation was created (a real write)...
        Assert.Contains(result.BackendSteps, step => step.Name == "deploy-operation-create" && step.MutatesState);
        // ...but it was NEVER submitted.
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
        Assert.DoesNotContain(result.BackendSteps, step => step.Name.StartsWith("deploy-operation-submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteSyncAsync_ServerParksAwaitingApproval_DirectAllowedStillDoesNotSubmit()
    {
        // Even with direct-allowed, if the server's deploy plan requires approval and parks the
        // operation at AwaitingApproval, the executor must not auto-submit it.
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-9a1", status = "AwaitingApproval" })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.AwaitingApproval, result.Status);
        Assert.Equal("op-9a1", result.OperationId);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
    }

    // ---- Invariant 3: execute + approved (direct-allowed) submits and polls to terminal. ----

    [Fact]
    public async Task ExecuteSyncAsync_ExecuteDirectAllowed_SubmitsAndPollsToTerminal()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Planned" });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Reconciling" });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/op-7f3", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Succeeded" });
            }

            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.Succeeded, result.Status);
        Assert.Equal("op-7f3", result.OperationId);
        Assert.True(result.Mutated);
        Assert.Contains(handler.CapturedRequests, request =>
            request.Method == "POST" && request.Uri.EndsWith("/op-7f3/submit", StringComparison.Ordinal));
        Assert.Contains(result.BackendSteps, step => step.Name == "deploy-operation-submit" && step.MutatesState);
        Assert.Contains(result.BackendSteps, step => step.Name.StartsWith("deploy-operation-poll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteSyncAsync_StillReconcilingAtPollBudget_ReportsInProgressNotFailed()
    {
        // The reconciler advances the operation server-side; a real deploy routinely takes
        // longer than the poll budget. A still-non-terminal status at budget exhaustion MUST
        // be reported as in-progress (healthy, keep watching), never as a failure. This is the
        // S1 regression guard for AUD-087.
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Planned" });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Reconciling" });
            }

            // The operation never reaches terminal within the budget.
            return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Reconciling" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        MutableTimeProvider clock = new();
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy(),
            pollPolicy: new DeployPollPolicy(
                Timeout: TimeSpan.FromSeconds(5),
                InitialInterval: TimeSpan.FromSeconds(1),
                MaxInterval: TimeSpan.FromSeconds(2)),
            // Advance the virtual clock instead of sleeping so the budget is exhausted instantly.
            delay: (interval, _) => { clock.Advance(interval); return Task.CompletedTask; },
            timeProvider: clock);

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.InProgress, result.Status);
        Assert.NotEqual(GitOpsExecutionStatus.Failed, result.Status);
        Assert.Equal("op-7f3", result.OperationId);
        Assert.Equal("Reconciling", result.ServerStatus);
        Assert.True(result.Mutated);
        // It actually polled more than once (backoff) before giving up the budget.
        Assert.True(result.BackendSteps.Count(step => step.Name.StartsWith("deploy-operation-poll", StringComparison.Ordinal)) > 1);
        Assert.Empty(result.BlockingReasons);
    }

    [Fact]
    public async Task ExecuteSyncAsync_TerminalFailed_StillReportsFailed()
    {
        // A genuine terminal failure must still resolve to Failed (the in-progress change must
        // not mask real terminal failures).
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Planned" });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Reconciling" });
            }

            return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Failed" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.Failed, result.Status);
        Assert.Equal("Failed", result.ServerStatus);
    }

    [Fact]
    public async Task ExecuteSyncAsync_SubmitRefused_ReportsApprovalRequiredAndDoesNotMutate()
    {
        // A 403 from the OperatorApprovalGate (or any submit failure) means nothing reconciled.
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Planned" });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Contains(result.BackendSteps, step => step.Name == "deploy-operation-submit" && !step.MutatesState);
    }

    [Fact]
    public async Task ExecuteSyncAsync_NoDeployTarget_StaysContractUnavailableNoInventedId()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: null),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.ContractUnavailable, result.Status);
        Assert.Null(result.OperationId);
        Assert.False(result.Mutated);
        Assert.Empty(handler.CapturedRequests);
    }

    // ---- Promotion reuses the same spine. ----

    [Fact]
    public async Task ExecutePromotionAsync_PrFirst_CreatesButPausesForApproval()
    {
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-promote", status = "AwaitingApproval" })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        PromotionExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            OperatorPolicyModel.Default);

        GitOpsExecutionResult result = await executor.ExecutePromotionAsync(
            "release/2026.03", null, "promote", "idem", "corr", "high", SyncParameters,
            authorizationDryRun: false, policyGate: "prod-promotion-gated", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.AwaitingApproval, result.Status);
        Assert.Equal("op-promote", result.OperationId);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
    }

    // ---- Invariant 4: data-affecting rollback without approval is refused. ----

    [Fact]
    public async Task ExecuteRollbackAsync_ReleaseCapabilityDisabled_IssuesNothing()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        RollbackExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await executor.ExecuteRollbackAsync(
            "op-7f3", "incident-123", authorizationDryRun: false, policyGate: "break-glass", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ExperimentalDisabled, result.Status);
        Assert.False(result.Mutated);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task ExecuteRollbackAsync_DataAffecting_RefusesEvenUnderDirectAllowed()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new
        {
            operationId = "op-7f3",
            status = "Succeeded",
            metadataRelease = new { rollbackPlan = new { @class = "SnapshotRestore", isDataAffecting = true } }
        }));
        using BackendGateway gateway = CreateGateway(handler);
        RollbackExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api", rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await executor.ExecuteRollbackAsync(
            "op-7f3", "incident-123", authorizationDryRun: false, policyGate: "break-glass", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        // SAFETY: the rollback endpoint was NEVER called for a data-affecting op.
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
        Assert.Contains("data-affecting-rollback-requires-approval", result.BlockingReasons);
    }

    [Fact]
    public async Task ExecuteRollbackAsync_UnknownClassification_TreatedAsDataAffectingAndRefused()
    {
        // No rollbackPlan -> server defaults to destructive; the executor mirrors that and refuses.
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "Succeeded" }));
        using BackendGateway gateway = CreateGateway(handler);
        RollbackExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api", rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await executor.ExecuteRollbackAsync(
            "op-7f3", "reason", authorizationDryRun: false, policyGate: "break-glass", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRollbackAsync_PlanMode_IssuesNothing()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        RollbackExecutor executor = new(
            CreateRuntime(ExecutionMode.Plan, deployTargetId: "prod-api", rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await executor.ExecuteRollbackAsync(
            "op-7f3", "reason", authorizationDryRun: true, policyGate: "plan-only", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.PlanOnly, result.Status);
        Assert.False(result.Mutated);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task ExecuteRollbackAsync_MetadataOnly_DirectAllowed_IssuesRollback()
    {
        // A non-data-affecting (MetadataOnly) rollback under direct-allowed may actuate.
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/rollback", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "RolledBack" });
            }

            return TestHttpMessageHandler.JsonOk(new
            {
                operationId = "op-7f3",
                status = "Succeeded",
                metadataRelease = new { rollbackPlan = new { @class = "MetadataOnly", isDataAffecting = false } }
            });
        });
        using BackendGateway gateway = CreateGateway(handler);
        RollbackExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api", rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await executor.ExecuteRollbackAsync(
            "op-7f3", "rollback metadata", authorizationDryRun: false, policyGate: "break-glass", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, result.Status);
        Assert.True(result.Mutated);
        Assert.Contains(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRollbackAsync_MetadataOnly_PrFirst_PausesForApproval()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new
        {
            operationId = "op-7f3",
            status = "Succeeded",
            metadataRelease = new { rollbackPlan = new { @class = "MetadataOnly", isDataAffecting = false } }
        }));
        using BackendGateway gateway = CreateGateway(handler);
        RollbackExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api", rollbackEnabled: true),
            gateway,
            OperatorPolicyModel.Default); // pr-first

        GitOpsExecutionResult result = await executor.ExecuteRollbackAsync(
            "op-7f3", "rollback metadata", authorizationDryRun: false, policyGate: "lower-env-execution", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.AwaitingApproval, result.Status);
        Assert.False(result.Mutated);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));
    }

    // A virtual clock the poll loop reads via TimeProvider; the injected delay advances it so
    // the poll budget can be exhausted deterministically without any real waiting.
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    // ---- Invariant 5: the desired-state write never precedes the operation + approval. ----

    [Fact]
    public async Task ExecuteSyncAsync_ExecutePrFirst_NeverAppliesTheDesiredState()
    {
        // The operation is created and parked for approval. Under the old ordering the
        // manifest apply had ALREADY been sent by this point; it must not be sent at all.
        TestHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-7f3", status = "AwaitingApproval" })
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            OperatorPolicyModel.Default);

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor, desiredState: DesiredState());

        Assert.Equal(GitOpsExecutionStatus.AwaitingApproval, result.Status);
        Assert.DoesNotContain(
            handler.CapturedRequests,
            request => request.Uri.Contains("/manifest/apply", StringComparison.Ordinal));
        Assert.DoesNotContain(result.BackendSteps, step => step.Name == "manifest-apply");
    }

    [Fact]
    public async Task ExecuteSyncAsync_ExecuteDirectAllowed_AppliesDesiredStateAfterTheOperationExists()
    {
        List<string> order = [];
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                order.Add(path);
            }

            return path.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-1", status = "Planned" })
                : TestHttpMessageHandler.JsonOk(new { operationId = "op-1", status = "Succeeded" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor, desiredState: DesiredState());

        Assert.Equal(GitOpsExecutionStatus.Succeeded, result.Status);

        int createIndex = order.FindIndex(path => path.EndsWith("/deploy/operations", StringComparison.Ordinal));
        int applyIndex = order.FindIndex(path => path.Contains("/manifest/apply", StringComparison.Ordinal));
        int submitIndex = order.FindIndex(path => path.EndsWith("/submit", StringComparison.Ordinal));

        Assert.True(createIndex >= 0, "The durable operation must be created.");
        Assert.True(applyIndex > createIndex, "The desired-state apply must follow operation creation.");
        Assert.True(submitIndex > applyIndex, "The submit must follow the desired-state apply.");
        Assert.Contains(result.BackendSteps, step => step.Name == "manifest-apply" && step.MutatesState && step.Success);
    }

    [Fact]
    public async Task ExecuteSyncAsync_DesiredStateApplyFails_ReportsIndeterminateAndDoesNotSubmit()
    {
        // The apply was issued but did not succeed: whether it took effect is unknown, so the
        // executor must not submit on top of it and must not collapse this into success.
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/manifest/apply", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("apply failed")
                };
            }

            return path.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-1", status = "Planned" })
                : TestHttpMessageHandler.JsonOk(new { operationId = "op-1", status = "Planned" });
        });
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor, desiredState: DesiredState());

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Contains("manifest-apply-unconfirmed", result.BlockingReasons);
        Assert.DoesNotContain(
            handler.CapturedRequests,
            request => request.Uri.EndsWith("/submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteSyncAsync_RetryOfTheSameRequest_IssuesAtMostOneSubmit()
    {
        // The spine's at-most-once ledger: a duplicate delivery of the same sealed request
        // resumes the original operation instead of submitting a second time.
        TestHttpMessageHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post
                ? TestHttpMessageHandler.JsonOk(new { operationId = "op-1", status = "Planned" })
                : TestHttpMessageHandler.JsonOk(new { operationId = "op-1", status = "Succeeded" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy());

        GitOpsExecutionResult first = await ExecuteSyncAsync(executor);
        GitOpsExecutionResult retry = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.Succeeded, first.Status);
        Assert.Equal(GitOpsExecutionStatus.AwaitingApproval, retry.Status);
        Assert.Equal(
            1,
            handler.CapturedRequests.Count(request => request.Uri.EndsWith("/submit", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExecuteSyncAsync_FailsClosed_WhenTheAuditReceiptSinkIsUnavailable()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(
            CreateRuntime(ExecutionMode.Execute, deployTargetId: "prod-api"),
            gateway,
            DirectAllowedPolicy() with { AuditHookTarget = "  " });

        GitOpsExecutionResult result = await ExecuteSyncAsync(executor);

        Assert.Equal(GitOpsExecutionStatus.ContractUnavailable, result.Status);
        Assert.False(result.Mutated);
        Assert.Contains("audit-sink-unavailable", result.BlockingReasons);
        Assert.Empty(handler.CapturedRequests);
    }

    private static ManifestApplyRequest DesiredState()
        => BackendGateway.BuildGitOpsManifestRequest(
            service: "roads-api",
            environments: ["dev"],
            revision: "release/2026.03",
            action: "sync",
            changeSummary: "ship roads-api",
            gitOpsTool: "honua-gitops",
            terraformRepository: "https://github.com/honua-io/honua-iac",
            terraformRef: "main",
            deploymentTargets: ["eks"],
            dryRun: false,
            executionMode: ExecutionMode.Execute,
            executionTier: ExecutionTier.ExecuteLowerEnv,
            allowedEnvironments: ["dev", "staging", "prod"]);

    private static Task<GitOpsExecutionResult> ExecuteSyncAsync(
        GitOpsExecutor executor,
        bool authorizationDryRun = false,
        ManifestApplyRequest? desiredState = null)
        => executor.ExecuteSyncAsync(
            desiredRevision: "release/2026.03",
            currentRevision: null,
            reason: "ship roads-api",
            idempotencyKey: "honua-devops:proposal:prod-api:roads-api:dev:release/2026.03:sync",
            correlationId: "honua-devops:sync:roads-api",
            priority: "normal",
            parameters: SyncParameters,
            authorizationDryRun: authorizationDryRun,
            policyGate: authorizationDryRun ? "plan-only" : "lower-env-execution",
            CancellationToken.None,
            desiredState);

    private static OperationRuntime CreateRuntime(
        ExecutionMode mode,
        string? deployTargetId,
        bool rollbackEnabled = false)
        => new(
            mode,
            mode == ExecutionMode.Execute ? ExecutionTier.ExecuteLowerEnv : ExecutionTier.Plan,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: deployTargetId,
            RollbackEnabled: rollbackEnabled);

    private static BackendGateway CreateGateway(TestHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
    }

    private static OperatorPolicyModel DirectAllowedPolicy()
        => new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);

    private static BackendConfiguration CreateBackendConfiguration()
        => new(
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
