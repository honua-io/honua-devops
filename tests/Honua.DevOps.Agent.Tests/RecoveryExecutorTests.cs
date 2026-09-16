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
public sealed class RecoveryExecutorTests : IDisposable
{
    // Every scenario starts from the approved desired intent the deployment approval recorded.
    // Restart scenarios share this file-backed ledger exactly as a restarted process would.
    private readonly string _ledgerDirectory = Directory.CreateTempSubdirectory("honua-devops-recovery-").FullName;
    private readonly FileDesiredIntentLedger _ledger;

    public RecoveryExecutorTests()
    {
        _ledger = new FileDesiredIntentLedger(Path.Combine(_ledgerDirectory, "audit.jsonl.desired-intent.jsonl"));
        Assert.Equal(DesiredIntentCommitStatus.Committed, _ledger
            .TryCommitAsync(0, ApprovedIntent("release/2026.03", "op-recover-1"), CancellationToken.None)
            .GetAwaiter().GetResult().Status);
    }

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
    public async Task ExecuteRecoveryAsync_CapabilityDisabled_IssuesNothing()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = CreateGateway(handler);
        ActuationSpine spine = new(ExecuteRuntime(protectedRecoveryEnabled: false), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);
        RecoveryExecutor executor = new(ExecuteRuntime(protectedRecoveryEnabled: false), gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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

        RecoveryExecutor first = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);
        GitOpsExecutionResult firstResult = await first.ExecuteRecoveryAsync(
            grant, "operator@honua.io", "prod-api", "observation-window-expired", CancellationToken.None);

        ActuationSpine restartedSpine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor retry = new(runtime, gateway, DirectAllowedPolicy(), restartedSpine, _ledger);
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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor first = new(runtime, gateway, DirectAllowedPolicy(), firstSpine, _ledger);
        GitOpsExecutionResult firstResult = await first.ExecuteRecoveryAsync(
            IssueGrant(firstSpine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        // No shared grant or claim ledger: reconstruct the declared scope and consume the
        // surviving server operation, as the restarted caller must do.
        ActuationSpine restartedSpine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor restarted = new(runtime, gateway, DirectAllowedPolicy(), restartedSpine, _ledger);
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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Contains("recovery-refused", result.BlockingReasons);
    }

    // honua-server#4958: the rollback body quotes the approved scope and the server's sealed
    // grant, so the server re-checks the exact recovery at admission.
    [Fact]
    public async Task ExecuteRecoveryAsync_SendsFenceQuotingApprovedScopeAndSealedGrant()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(
            Operation(request.Method == HttpMethod.Post ? "RollbackRequested" : "Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine, _ledger)
            .ExecuteRecoveryAsync(grant, "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        CapturedRequest post = Assert.Single(handler.CapturedRequests, request => request.Method == "POST");
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(post.Body!);
        System.Text.Json.JsonElement root = body.RootElement;
        Assert.Equal(
            ["actor", "compensation", "expectedCandidateRevision", "expectedPreviousRevision", "expectedProtectionPhase",
                "grantId", "notAfter", "policyDigest", "reason", "targetId"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("prod-api", root.GetProperty("targetId").GetString());
        Assert.Equal("release/2026.03", root.GetProperty("expectedCandidateRevision").GetString());
        Assert.Equal("release/2026.02", root.GetProperty("expectedPreviousRevision").GetString());
        Assert.Equal("observing", root.GetProperty("expectedProtectionPhase").GetString());
        Assert.Equal(SealedGrantId, root.GetProperty("grantId").GetString());
        Assert.Equal("sha256:policy-digest", root.GetProperty("policyDigest").GetString());
        Assert.Equal(SealedActor, root.GetProperty("actor").GetString());
        Assert.Equal(grant.ExpiresAtUtc, root.GetProperty("notAfter").GetDateTimeOffset());
        Assert.Equal("restore-previous-revision", root.GetProperty("compensation").GetString());
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_SealedTenant_IsQuotedBack()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            System.Text.Json.Nodes.JsonObject operation = System.Text.Json.JsonSerializer.SerializeToNode(
                Operation(request.Method == HttpMethod.Post ? "RollbackRequested" : "Reconciling"))!.AsObject();
            operation["protection"]!["tenantId"] = "tenant-a";
            return TestHttpMessageHandler.JsonOk(operation);
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());

        await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        CapturedRequest post = Assert.Single(handler.CapturedRequests, request => request.Method == "POST");
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(post.Body!);
        Assert.Equal("tenant-a", body.RootElement.GetProperty("tenantId").GetString());
    }

    // A window the server never sealed (the pre-#4963 shape), one that permits a different
    // compensation, or one sealed for another tenant than the approval (the grant is issued for
    // tenant-a) cannot be fenced, so nothing is requested and no in-process claim is spent.
    [Theory]
    [InlineData("grantId", null)]
    [InlineData("actor", null)]
    [InlineData("permittedCompensation", null)]
    [InlineData("permittedCompensation", "delete-everything")]
    [InlineData("tenantId", "tenant-b")]
    public async Task ExecuteRecoveryAsync_UnsealedOrBroadenedServerGrant_IsRefusedWithoutRequest(string field, string? value)
    {
        TestHttpMessageHandler handler = new(_ =>
        {
            System.Text.Json.Nodes.JsonObject operation = System.Text.Json.JsonSerializer.SerializeToNode(Operation("Reconciling"))!.AsObject();
            System.Text.Json.Nodes.JsonObject protection = operation["protection"]!.AsObject();
            if (value is null)
            {
                protection.Remove(field);
            }
            else
            {
                protection[field] = value;
            }

            return TestHttpMessageHandler.JsonOk(operation);
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        GitOpsExecutionResult result = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine, _ledger)
            .ExecuteRecoveryAsync(grant, "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Equal(["recovery-grant-unsealed"], result.BlockingReasons);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Method == "POST");
        Assert.True(spine.TryAuthorizeRecovery(grant, "operator@honua.io", "prod-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision, out _, out _));
    }

    // Refusal codes and statuses are the ones honua-server returned live on nightly-2cc2213.
    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "recovery_fence_expired")]
    [InlineData(HttpStatusCode.Conflict, "recovery_fence_grant_mismatch")]
    [InlineData(HttpStatusCode.Conflict, "recovery_fence_candidate_revision_mismatch")]
    [InlineData(HttpStatusCode.Forbidden, "recovery_fence_actor_mismatch")]
    [InlineData(HttpStatusCode.BadRequest, "recovery_fence_unknown_property")]
    public async Task ExecuteRecoveryAsync_ServerFenceRefusal_IsDefiniteAndRecordsNothing(HttpStatusCode status, string code)
    {
        TestHttpMessageHandler handler = new(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new { status = (int)status, detail = "refused at admission", code })
            }
            : TestHttpMessageHandler.JsonOk(Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());

        GitOpsExecutionResult result = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Equal(["recovery-fence-refused", code], result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        DesiredIntentSnapshot intent = await _ledger.ReadLatestAsync("prod-api", CancellationToken.None);
        Assert.Equal((1L, DesiredIntentKind.Approved), (intent.Latest!.Version, intent.Latest.Kind));
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
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.False(result.Mutated);
        Assert.Contains("recovery-protection-unavailable", result.BlockingReasons);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Method == "POST");
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_VerifiedRecovery_RecordsRestoredIntentQuarantineAndLineage()
    {
        const string commitSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(request.Method == HttpMethod.Post
            ? OperationWithCommit("RolledBack", commitSha)
            : Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, result.Status);
        Assert.Equal(RolloutJourneyStatus.PreviousVersionRestored, RolloutJourneyStatus.From(result));
        DesiredIntentRecord restored = (await _ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;
        Assert.Equal(2, restored.Version);
        Assert.Equal(DesiredIntentKind.Restored, restored.Kind);
        Assert.Equal("release/2026.02", restored.DesiredRevision);
        Assert.Equal(["release/2026.03"], restored.RejectedRevisions);
        Assert.Equal("op-recover-1", restored.OperationId);
        Assert.Equal("approval-receipt-op-recover-1", restored.ApprovalReference);
        Assert.Equal("operator@honua.io", restored.Actor);
        Assert.Equal(commitSha, restored.CommitSha);
        Assert.Contains(result.Findings, finding => finding.Contains("restores `release/2026.02` and quarantines `release/2026.03`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_NewerApprovedIntent_RefusesBeforeAnyBackendCall()
    {
        await CommitAsync(1, ApprovedIntent("release/2026.04", "op-newer-2"));
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.Contains("recovery-intent-superseded", result.BlockingReasons);
        Assert.Empty(handler.CapturedRequests);
        DesiredIntentRecord latest = (await _ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;
        Assert.Equal((2, DesiredIntentKind.Approved, "release/2026.04"), (latest.Version, latest.Kind, latest.DesiredRevision));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_NewerApprovalRecordedDuringRecovery_ConflictsAndNeverClaimsRestoration()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                // A newer approval lands after recovery read its compare-and-set basis.
                Assert.Equal(DesiredIntentCommitStatus.Committed,
                    _ledger.TryCommitAsync(1, ApprovedIntent("release/2026.04", "op-newer-2"), CancellationToken.None).GetAwaiter().GetResult().Status);
                return TestHttpMessageHandler.JsonOk(Operation("RolledBack"));
            }

            return TestHttpMessageHandler.JsonOk(Operation("Reconciling"));
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        RecoveryExecutor executor = new(runtime, gateway, DirectAllowedPolicy(), spine, _ledger);

        GitOpsExecutionResult result = await executor.ExecuteRecoveryAsync(
            IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, result.Status);
        Assert.Contains("desired-intent-conflict", result.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(result));
        Assert.True(result.Mutated);
        DesiredIntentRecord latest = (await _ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;
        Assert.Equal((2, DesiredIntentKind.Approved, "release/2026.04"), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Empty(latest.RejectedRevisions);
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("quarantines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_WithoutDurableOrRecordedIntent_IssuesNothing()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(Operation("Reconciling")));
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());

        // The stdout audit target has no durable ledger.
        GitOpsExecutionResult noLedger = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine)
            .ExecuteRecoveryAsync(IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);
        FileDesiredIntentLedger empty = new(Path.Combine(_ledgerDirectory, "empty.desired-intent.jsonl"));
        GitOpsExecutionResult unrecorded = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), spine, empty)
            .ExecuteRecoveryAsync(IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.ContractUnavailable, noLedger.Status);
        Assert.Contains("desired-intent-ledger-unavailable", noLedger.BlockingReasons);
        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, unrecorded.Status);
        Assert.Contains("recovery-intent-unrecorded", unrecorded.BlockingReasons);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task ExecuteRecoveryAsync_LedgerWriteFailsThenRestart_RecordsOnceWithoutSecondCompensation()
    {
        int providerMutations = 0;
        string durableStatus = "Reconciling";
        TestHttpMessageHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                providerMutations++;
                durableStatus = "RolledBack";
            }

            return TestHttpMessageHandler.JsonOk(Operation(durableStatus));
        });
        using BackendGateway gateway = CreateGateway(handler);
        OperationRuntime runtime = ExecuteRuntime(true);

        ActuationSpine firstSpine = new(runtime, DirectAllowedPolicy());
        GitOpsExecutionResult failed = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), firstSpine, new FailingCommitLedger(_ledger))
            .ExecuteRecoveryAsync(IssueGrant(firstSpine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);
        DesiredIntentRecord afterFailure = (await _ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;

        ActuationSpine restartedSpine = new(runtime, DirectAllowedPolicy());
        GitOpsExecutionResult restarted = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), restartedSpine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(restartedSpine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);
        ActuationSpine replayedSpine = new(runtime, DirectAllowedPolicy());
        GitOpsExecutionResult replayed = await new RecoveryExecutor(runtime, gateway, DirectAllowedPolicy(), replayedSpine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(replayedSpine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, failed.Status);
        Assert.Contains("desired-intent-write-failed", failed.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(failed));
        Assert.Equal((1, DesiredIntentKind.Approved), (afterFailure.Version, afterFailure.Kind));
        Assert.Equal(GitOpsExecutionStatus.RolledBack, restarted.Status);
        Assert.False(restarted.Mutated);
        Assert.Equal(GitOpsExecutionStatus.RolledBack, replayed.Status);
        Assert.Equal(1, providerMutations);
        DesiredIntentRecord latest = (await _ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;
        Assert.Equal((2, DesiredIntentKind.Restored, "release/2026.02"), (latest.Version, latest.Kind, latest.DesiredRevision));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_ledgerDirectory, "audit.jsonl.desired-intent.jsonl")).Length);
    }

    [Fact]
    public async Task NextReconcileAfterRecovery_RefusesQuarantinedCandidateAndCarriesQuarantineForward()
    {
        TestHttpMessageHandler recoveryHandler = new(request => TestHttpMessageHandler.JsonOk(
            Operation(request.Method == HttpMethod.Post ? "RolledBack" : "Reconciling")));
        using BackendGateway recoveryGateway = CreateGateway(recoveryHandler);
        OperationRuntime runtime = ExecuteRuntime(true);
        ActuationSpine spine = new(runtime, DirectAllowedPolicy());
        GitOpsExecutionResult recovery = await new RecoveryExecutor(runtime, recoveryGateway, DirectAllowedPolicy(), spine, _ledger)
            .ExecuteRecoveryAsync(IssueGrant(spine), "operator@honua.io", "prod-api", "health-regression", CancellationToken.None);
        Assert.Equal(GitOpsExecutionStatus.RolledBack, recovery.Status);

        TestHttpMessageHandler syncHandler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-fix-3", status = "Planned" });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-fix-3", status = "Reconciling" });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/op-fix-3", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    operationId = "op-fix-3",
                    status = "Succeeded",
                    actuatorReceipt = new { receiptId = "rcpt-fix-3", operationId = "op-fix-3" }
                });
            }

            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        using BackendGateway syncGateway = CreateGateway(syncHandler);
        GitOpsExecutor sync = new(runtime, syncGateway, DirectAllowedPolicy(), intentLedger: _ledger);

        GitOpsExecutionResult resurrect = await SyncAsync(sync, "release/2026.03");
        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, resurrect.Status);
        Assert.Contains("desired-revision-quarantined", resurrect.BlockingReasons);
        Assert.False(resurrect.Mutated);
        Assert.Empty(syncHandler.CapturedRequests);

        GitOpsExecutionResult forward = await SyncAsync(sync, "release/2026.04");
        Assert.Equal(GitOpsExecutionStatus.Succeeded, forward.Status);
        DesiredIntentRecord latest = (await _ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;
        Assert.Equal((3, DesiredIntentKind.Approved, "release/2026.04", "op-fix-3"),
            (latest.Version, latest.Kind, latest.DesiredRevision, latest.OperationId));
        Assert.Equal(["release/2026.03"], latest.RejectedRevisions);
    }

    [Fact]
    public async Task ExecuteSubmitAsync_QuarantinedOperationRevision_RefusesBeforeSubmit()
    {
        await CommitAsync(1, ApprovedIntent("release/2026.02", "op-recover-1", ["release/2026.03"]) with { Kind = DesiredIntentKind.Restored });
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new
        {
            operationId = "op-resume",
            status = "Planned",
            target = new { targetId = "prod-api", desiredRevision = "release/2026.03" }
        }));
        using BackendGateway gateway = CreateGateway(handler);
        GitOpsExecutor executor = new(ExecuteRuntime(true), gateway, DirectAllowedPolicy(), intentLedger: _ledger);

        GitOpsExecutionResult result = await executor.ExecuteSubmitAsync(
            "op-resume", "approved", true, false, "prod-promotion-gated", CancellationToken.None, "approval-receipt-9");

        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, result.Status);
        Assert.Contains("desired-revision-quarantined", result.BlockingReasons);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Method == "POST");
    }

    private async Task CommitAsync(long expectedVersion, DesiredIntentRecord record)
        => Assert.Equal(DesiredIntentCommitStatus.Committed,
            (await _ledger.TryCommitAsync(expectedVersion, record, CancellationToken.None)).Status);

    private static DesiredIntentRecord ApprovedIntent(string revision, string operationId, IReadOnlyList<string>? rejected = null)
        => new("prod-api", 0, DesiredIntentKind.Approved, revision, rejected ?? [], operationId,
            $"approval-receipt-{operationId}", "operator@honua.io", null, DateTimeOffset.UtcNow);

    private static Task<GitOpsExecutionResult> SyncAsync(GitOpsExecutor executor, string revision)
        => executor.ExecuteSyncAsync(
            desiredRevision: revision,
            currentRevision: null,
            reason: "ship prod-api",
            idempotencyKey: $"honua-devops:proposal:prod-api:{revision}:sync",
            correlationId: "honua-devops:sync:prod-api",
            priority: "normal",
            parameters: new Dictionary<string, string> { ["service"] = "prod-api", ["environments"] = "dev", ["action"] = "sync" },
            authorizationDryRun: false,
            policyGate: "lower-env-execution",
            CancellationToken.None);

    private static System.Text.Json.Nodes.JsonObject OperationWithCommit(string status, string commitSha)
    {
        System.Text.Json.Nodes.JsonObject operation = System.Text.Json.JsonSerializer.SerializeToNode(Operation(status))!.AsObject();
        operation["metadataRelease"] = new System.Text.Json.Nodes.JsonObject { ["commitSha"] = commitSha };
        return operation;
    }

    private sealed class FailingCommitLedger(IDesiredIntentLedger inner) : IDesiredIntentLedger
    {
        public Task<DesiredIntentSnapshot> ReadLatestAsync(string target, CancellationToken cancellationToken)
            => inner.ReadLatestAsync(target, cancellationToken);

        public Task<DesiredIntentCommitResult> TryCommitAsync(long expectedVersion, DesiredIntentRecord next, CancellationToken cancellationToken)
            => Task.FromResult(new DesiredIntentCommitResult(DesiredIntentCommitStatus.WriteFailed, null, "injected: no space left on device"));
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
            protection = new
            {
                phase,
                previousRevision = prior,
                candidateRevision = protectedCandidate ?? candidate,
                policyDigest = policy,
                observationDeadline = "2099-01-01T00:00:00Z",
                // The grant honua-server seals at exposure (#4958/#4963); shape observed live on nightly-2cc2213.
                grantId = SealedGrantId,
                actor = SealedActor,
                permittedCompensation = "restore-previous-revision"
            }
        };

    private const string SealedGrantId = "grant-7c01359bc42d2d0355015f2440cd8fd3";
    private const string SealedActor = "admin";

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
