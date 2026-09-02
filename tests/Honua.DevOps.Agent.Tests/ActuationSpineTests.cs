using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// The durable actuation spine (issue #153). These tests prove the invariant that makes the
// ordering fix real: a mutating backend call cannot happen before a durable operation and
// its authorization exist, because the write authority (a grant) cannot be obtained first.
public class ActuationSpineTests
{
    // ---- Sealing the request identity ----

    [Fact]
    public void Authorize_SealsTargetDigestIdempotencyKeyPolicyAndActor()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());

        ActuationAuthorization authorization = spine.Authorize(SyncRequest());

        Assert.True(authorization.IsGranted);
        ActuationSpine.OperationGrant grant = Assert.IsType<ActuationSpine.OperationGrant>(authorization.Grant);
        Assert.Equal("honua.gitops.sync", grant.ActuatorId);
        Assert.Equal("prod-api", grant.Target);
        Assert.Equal("honua-devops:sync:roads-api:dev", grant.IdempotencyKey);
        Assert.Equal("lower-env-execution", grant.PolicyGate);
        Assert.Equal("operator@honua.io", grant.Actor);
        Assert.Equal(64, grant.RequestDigest.Length);
    }

    [Fact]
    public void ComputeDigest_IsDeterministicForTheSameLogicalRequest()
    {
        Assert.Equal(
            ActuationSpine.ComputeDigest(SyncRequest()),
            ActuationSpine.ComputeDigest(SyncRequest()));

        Assert.NotEqual(
            ActuationSpine.ComputeDigest(SyncRequest()),
            ActuationSpine.ComputeDigest(SyncRequest() with { DesiredState = "revision=other" }));
    }

    // ---- Fail closed before anything is written ----

    [Fact]
    public void Authorize_FailsClosed_WhenNoDurableTargetIsConfigured()
    {
        ActuationSpine spine = new(ExecuteRuntime(deployTargetId: null), DirectAllowedPolicy());

        ActuationAuthorization authorization = spine.Authorize(SyncRequest() with { Target = string.Empty });

        Assert.False(authorization.IsGranted);
        Assert.Null(authorization.Grant);
        Assert.Equal(ActuationOutcome.ContractUnavailable, authorization.Outcome);
        Assert.Equal("deploy-target-unconfigured", authorization.BlockingReason);
    }

    [Fact]
    public void Authorize_FailsClosed_WhenTheAuditReceiptSinkIsUnavailable()
    {
        // A mutation whose evidence cannot be recorded is refused before it starts.
        OperatorPolicyModel noAuditSink = DirectAllowedPolicy() with { AuditHookTarget = "  " };
        ActuationSpine spine = new(ExecuteRuntime(), noAuditSink);

        ActuationAuthorization authorization = spine.Authorize(SyncRequest());

        Assert.False(authorization.IsGranted);
        Assert.Equal("audit-sink-unavailable", authorization.BlockingReason);
    }

    [Fact]
    public void Authorize_FailsClosed_WhenTheConfiguredAuditFileCannotBeOpened()
    {
        string missingParent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "audit.jsonl");
        OperatorPolicyModel policy = DirectAllowedPolicy() with { AuditHookTarget = $"file://{missingParent}" };
        ActuationSpine spine = new(ExecuteRuntime(), policy);

        ActuationAuthorization authorization = spine.Authorize(SyncRequest());

        Assert.False(authorization.IsGranted);
        Assert.Equal("audit-sink-unavailable", authorization.BlockingReason);
    }

    [Fact]
    public void Authorize_FailsClosed_WhenNoIdempotencyKeyIsSupplied()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());

        ActuationAuthorization authorization = spine.Authorize(SyncRequest() with { IdempotencyKey = "" });

        Assert.False(authorization.IsGranted);
        Assert.Equal("idempotency-key-missing", authorization.BlockingReason);
    }

    // ---- Stage 2: no mutation without an operation id AND a satisfied approval ----

    [Fact]
    public void TryAuthorizeMutation_Refuses_WhenTheControlPlaneReturnedNoOperationId()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;

        bool authorized = spine.TryAuthorizeMutation(
            grant,
            BackendMutation.DeployOperationSubmit,
            operationId: null,
            ApprovalEvidence.NotRequired("test"),
            out ActuationSpine.MutationGrant? mutation,
            out string refusal);

        Assert.False(authorized);
        Assert.Null(mutation);
        Assert.Contains("invented id", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeMutation_Refuses_WhenTheControlPlaneParkedTheOperationForApproval()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;

        bool authorized = spine.TryAuthorizeMutation(
            grant,
            BackendMutation.DeployOperationSubmit,
            "op-1",
            ApprovalEvidence.FromControlPlane("op-1", awaitingApproval: true, blockingReasons: []),
            out _,
            out string refusal);

        Assert.False(authorized);
        Assert.Contains("AwaitingApproval", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeMutation_Refuses_UnderThePrFirstPolicyCeiling()
    {
        // pr-first is not a registered direct-execution policy result, so it can never
        // satisfy the gate on its own — the operator must approve externally.
        ActuationSpine spine = new(ExecuteRuntime(), OperatorPolicyModel.Default);
        ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;

        bool authorized = spine.TryAuthorizeMutation(
            grant,
            BackendMutation.DeployOperationSubmit,
            "op-1",
            ApprovalEvidence.FromDirectExecutionPolicy(grant.Decision),
            out _,
            out string refusal);

        Assert.False(authorized);
        Assert.Contains("pr-first", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeMutation_Refuses_UnderPlanPostureEvenWithApproval()
    {
        // The local execution policy is a safety ceiling: it can deny, never grant.
        ActuationSpine spine = new(PlanRuntime(), DirectAllowedPolicy());
        ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;

        bool authorized = spine.TryAuthorizeMutation(
            grant,
            BackendMutation.DeployOperationSubmit,
            "op-1",
            ApprovalEvidence.NotRequired("approved out of band"),
            out _,
            out string refusal);

        Assert.False(authorized);
        Assert.Contains("EXECUTION_MODE=plan", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeMutation_BindsTheGrantToTheOperationDigestAndApproval()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;

        Assert.True(spine.TryAuthorizeMutation(
            grant,
            BackendMutation.DeployOperationSubmit,
            "op-42",
            ApprovalEvidence.FromDirectExecutionPolicy(grant.Decision),
            out ActuationSpine.MutationGrant? mutation,
            out _));

        Assert.Equal("op-42", mutation!.OperationId);
        Assert.Equal(grant.RequestDigest, mutation.RequestDigest);
        Assert.Equal(grant.IdempotencyKey, mutation.IdempotencyKey);
        Assert.Equal("direct-execution-policy", mutation.Approval.Kind);
        Assert.Equal(BackendMutation.DeployOperationSubmit, mutation.Mutation);
    }

    // ---- At most one mutation per sealed request ----

    [Fact]
    public void TryAuthorizeMutation_IsSingleUsePerIdempotencyKeyAndRoute()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.OperationGrant first = spine.Authorize(SyncRequest()).Grant!;
        ActuationSpine.OperationGrant retry = spine.Authorize(SyncRequest()).Grant!;

        Assert.True(spine.TryAuthorizeMutation(
            first,
            BackendMutation.DeployOperationSubmit,
            "op-1",
            ApprovalEvidence.NotRequired("ok"),
            out _,
            out _));

        // A retry of the same sealed request must observe the original operation, not mint a
        // second write authority for it.
        Assert.False(spine.TryAuthorizeMutation(
            retry,
            BackendMutation.DeployOperationSubmit,
            "op-2",
            ApprovalEvidence.NotRequired("ok"),
            out ActuationSpine.MutationGrant? second,
            out string refusal));

        Assert.Null(second);
        Assert.Contains("already claimed", refusal, StringComparison.Ordinal);
        Assert.Contains("op-1", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeMutation_ConcurrentDelivery_AuthorizesExactlyOneMutation()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());

        int authorized = 0;
        Parallel.For(0, 16, index =>
        {
            _ = index;
            ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;
            if (spine.TryAuthorizeMutation(
                    grant,
                    BackendMutation.DeployOperationSubmit,
                    "op-1",
                    ApprovalEvidence.NotRequired("ok"),
                    out _,
                    out _))
            {
                Interlocked.Increment(ref authorized);
            }
        });

        Assert.Equal(1, authorized);
    }

    [Fact]
    public void ARestartCarriesLineageThroughTheDeterministicIdempotencyKey()
    {
        // The in-process ledger is empty after a restart by construction. What survives is
        // the sealed idempotency key and request digest: a fresh spine re-derives exactly the
        // same identity for the same logical request, so the create call resolves back to the
        // control plane's original operation rather than opening a second one.
        ActuationSpine before = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine afterRestart = new(ExecuteRuntime(), DirectAllowedPolicy());

        ActuationSpine.OperationGrant original = before.Authorize(SyncRequest()).Grant!;
        ActuationSpine.OperationGrant resumed = afterRestart.Authorize(SyncRequest()).Grant!;

        Assert.Equal(original.IdempotencyKey, resumed.IdempotencyKey);
        Assert.Equal(original.RequestDigest, resumed.RequestDigest);
    }

    // ---- Grants are route-bound and cannot be forged ----

    [Fact]
    public void EnsureAuthorizes_RejectsAGrantPresentedForADifferentRoute()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.OperationGrant grant = spine.Authorize(SyncRequest()).Grant!;
        spine.TryAuthorizeMutation(
            grant,
            BackendMutation.DeployOperationSubmit,
            "op-1",
            ApprovalEvidence.NotRequired("ok"),
            out ActuationSpine.MutationGrant? mutation,
            out _);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => mutation!.EnsureAuthorizes(BackendMutation.ManifestApply));
        Assert.Contains("does not authorize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Issue_RejectsAGrantMintedWithoutTheSpinesIssuanceSeal()
    {
        // The seal is a private static of ActuationSpine, so no caller can present it. This
        // is what stops a mutating backend call from manufacturing its own authority.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationSpine.OperationGrant.Issue(
                seal: new object(),
                actuatorId: "forged",
                action: "sync",
                target: "prod-api",
                requestDigest: "0",
                idempotencyKey: "k",
                policyGate: "lower-env-execution",
                actor: "attacker",
                lifecycleEntry: BackendMutation.DeployOperationCreate,
                decision: Honua.DevOps.Agent.Operations.GitOps.GitOpsActuationDecision.PlanOnly("direct-allowed", "gate", "why")));

        Assert.Contains("may only be issued by ActuationSpine", exception.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static ActuationRequest SyncRequest()
        => new(
            ActuatorId: "honua.gitops.sync",
            Action: "sync",
            Target: "prod-api",
            Environments: ["dev"],
            DesiredState: "revision=release/2026.03",
            IdempotencyKey: "honua-devops:sync:roads-api:dev",
            PolicyGate: "lower-env-execution",
            AuthorizationDryRun: false,
            Actor: "operator@honua.io");

    private static OperationRuntime ExecuteRuntime(string? deployTargetId = "prod-api")
        => OperationRuntime.SafeDefault with
        {
            ExecutionMode = ExecutionMode.Execute,
            ExecutionTier = ExecutionTier.ExecuteLowerEnv,
            DeployTargetId = deployTargetId
        };

    private static OperationRuntime PlanRuntime()
        => OperationRuntime.SafeDefault with { DeployTargetId = "prod-api" };

    private static OperatorPolicyModel DirectAllowedPolicy()
        => new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
}
