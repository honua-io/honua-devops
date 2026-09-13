using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// Bounded deployment recovery (issue #191): a DeploymentRecoveryGrant binds actor/tenant/
// target/prior+candidate revisions/safety-policy digest/expiry/compensation at the moment a
// deployment is approved. These tests prove the grant is scoped exactly as declared: a
// recovery request for a different actor, target, compensation, an expired grant, or a
// candidate revision the operation no longer shows is refused, and a valid request dedupes
// through the same at-most-once ledger every other actuation uses.
public class DeploymentRecoveryGrantTests
{
    // ---- Minting the grant ----

    [Fact]
    public void AuthorizeRecoveryGrant_SealsTheDeclaredScope()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());

        bool authorized = spine.AuthorizeRecoveryGrant(
            RecoveryRequest(),
            tenant: "tenant-a",
            priorRevision: "release/2026.02",
            candidateRevision: "release/2026.03",
            safetyPolicyDigest: "sha256:policy-digest",
            expiresAtUtc: FixedNow().AddMinutes(30),
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            operationId: "op-recover-1",
            out ActuationSpine.DeploymentRecoveryGrant? grant,
            out string refusal,
            FixedTimeProvider());

        Assert.True(authorized);
        Assert.Equal(string.Empty, refusal);
        Assert.Equal("operator@honua.io", grant!.Actor);
        Assert.Equal("tenant-a", grant.Tenant);
        Assert.Equal("prod-api", grant.Target);
        Assert.Equal("release/2026.02", grant.PriorRevision);
        Assert.Equal("release/2026.03", grant.CandidateRevision);
        Assert.Equal("op-recover-1", grant.OperationId);
        Assert.Equal(ActuationSpine.PermittedCompensation.RestorePriorRevision, grant.Compensation);
    }

    [Fact]
    public void AuthorizeRecoveryGrant_FailsClosed_WhenNoDurableTargetIsConfigured()
    {
        ActuationSpine spine = new(ExecuteRuntime(deployTargetId: null), DirectAllowedPolicy());

        bool authorized = spine.AuthorizeRecoveryGrant(
            RecoveryRequest() with { Target = string.Empty },
            "tenant-a", "release/2026.02", "release/2026.03", "sha256:policy-digest",
            FixedNow().AddMinutes(30), ActuationSpine.PermittedCompensation.RestorePriorRevision,
            "op-recover-1", out ActuationSpine.DeploymentRecoveryGrant? grant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(grant);
        Assert.Contains("TARGET_ID", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRecoveryGrant_Refuses_WhenPlanPostureNeverMutates()
    {
        ActuationSpine spine = new(PlanRuntime(), DirectAllowedPolicy());

        bool authorized = spine.AuthorizeRecoveryGrant(
            RecoveryRequest() with { AuthorizationDryRun = true },
            "tenant-a", "release/2026.02", "release/2026.03", "sha256:policy-digest",
            FixedNow().AddMinutes(30), ActuationSpine.PermittedCompensation.RestorePriorRevision,
            "op-recover-1", out ActuationSpine.DeploymentRecoveryGrant? grant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(grant);
    }

    [Fact]
    public void AuthorizeRecoveryGrant_Refuses_WhenExpiryIsNotInTheFuture()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());

        bool authorized = spine.AuthorizeRecoveryGrant(
            RecoveryRequest(),
            "tenant-a", "release/2026.02", "release/2026.03", "sha256:policy-digest",
            FixedNow(), ActuationSpine.PermittedCompensation.RestorePriorRevision,
            "op-recover-1", out ActuationSpine.DeploymentRecoveryGrant? grant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(grant);
        Assert.Contains("expiry", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRecoveryGrant_Refuses_WhenARequiredScopeFieldIsMissing()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());

        bool authorized = spine.AuthorizeRecoveryGrant(
            RecoveryRequest(),
            tenant: "  ",
            priorRevision: "release/2026.02",
            candidateRevision: "release/2026.03",
            safetyPolicyDigest: "sha256:policy-digest",
            expiresAtUtc: FixedNow().AddMinutes(30),
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            operationId: "op-recover-1",
            out ActuationSpine.DeploymentRecoveryGrant? grant,
            out string refusal,
            FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(grant);
    }

    // ---- Re-verifying a recovery request against the sealed grant ----

    [Fact]
    public void TryAuthorizeRecovery_Grants_WhenEveryBoundFieldMatches()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        bool authorized = spine.TryAuthorizeRecovery(
            grant,
            requestedActor: "operator@honua.io",
            requestedTarget: "prod-api",
            observedCandidateRevision: "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? mutationGrant,
            out string refusal,
            FixedTimeProvider());

        Assert.True(authorized);
        Assert.NotNull(mutationGrant);
        Assert.Equal("op-recover-1", mutationGrant!.OperationId);
        mutationGrant.EnsureAuthorizes(BackendMutation.DeployOperationRollback);
    }

    [Fact]
    public void TryAuthorizeRecovery_Refuses_WrongActor()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        bool authorized = spine.TryAuthorizeRecovery(
            grant, "someone-else@honua.io", "prod-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? mutationGrant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(mutationGrant);
        Assert.Contains("actor", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAuthorizeRecovery_Refuses_WrongTarget()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        bool authorized = spine.TryAuthorizeRecovery(
            grant, "operator@honua.io", "staging-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? mutationGrant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(mutationGrant);
        Assert.Contains("target", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAuthorizeRecovery_Refuses_ExpiredGrant()
    {
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        // Advance the clock past the grant's expiry.
        MutableTimeProvider clock = FixedTimeProvider();
        clock.Advance(TimeSpan.FromHours(1));

        bool authorized = spine.TryAuthorizeRecovery(
            grant, "operator@honua.io", "prod-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? mutationGrant, out string refusal, clock);

        Assert.False(authorized);
        Assert.Null(mutationGrant);
        Assert.Contains("expired", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAuthorizeRecovery_Refuses_BroadenedCompensation()
    {
        // The grant authorizes RestorePriorRevision only. A request for a different
        // compensation is a widening attempt and must be refused, even though every other
        // bound field (actor/target/candidate) matches.
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        bool authorized = spine.TryAuthorizeRecovery(
            grant, "operator@honua.io", "prod-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.QuarantineCandidateOnly,
            out ActuationSpine.MutationGrant? mutationGrant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(mutationGrant);
        Assert.Contains("authorizes", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAuthorizeRecovery_Refuses_CandidateRevisionHasMovedSinceApproval()
    {
        // Compare-and-set: a newer approved intent already changed the candidate on the
        // operation. Recovery must never overwrite it.
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        bool authorized = spine.TryAuthorizeRecovery(
            grant, "operator@honua.io", "prod-api", observedCandidateRevision: "release/2026.04",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? mutationGrant, out string refusal, FixedTimeProvider());

        Assert.False(authorized);
        Assert.Null(mutationGrant);
        Assert.Contains("superseded", refusal, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Restart/retry: at most one compensation per grant ----

    [Fact]
    public void TryAuthorizeRecovery_SecondAttemptForTheSameGrant_ObservesTheOriginalClaim()
    {
        // Simulates a restart/retry: the same idempotency-keyed grant is presented twice. Only
        // the first attempt may claim the mutation; the second observes it instead of a second
        // compensation being authorized.
        ActuationSpine spine = new(ExecuteRuntime(), DirectAllowedPolicy());
        ActuationSpine.DeploymentRecoveryGrant grant = IssueGrant(spine);

        bool first = spine.TryAuthorizeRecovery(
            grant, "operator@honua.io", "prod-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? firstGrant, out _, FixedTimeProvider());
        bool second = spine.TryAuthorizeRecovery(
            grant, "operator@honua.io", "prod-api", "release/2026.03",
            ActuationSpine.PermittedCompensation.RestorePriorRevision,
            out ActuationSpine.MutationGrant? secondGrant, out string secondRefusal, FixedTimeProvider());

        Assert.True(first);
        Assert.NotNull(firstGrant);
        Assert.False(second);
        Assert.Null(secondGrant);
        Assert.Contains("already claimed", secondRefusal, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    private static ActuationSpine.DeploymentRecoveryGrant IssueGrant(ActuationSpine spine)
    {
        Assert.True(spine.AuthorizeRecoveryGrant(
            RecoveryRequest(),
            "tenant-a", "release/2026.02", "release/2026.03", "sha256:policy-digest",
            FixedNow().AddMinutes(30), ActuationSpine.PermittedCompensation.RestorePriorRevision,
            "op-recover-1", out ActuationSpine.DeploymentRecoveryGrant? grant, out _, FixedTimeProvider()));
        return grant!;
    }

    private static ActuationRequest RecoveryRequest()
        => new(
            ActuatorId: "honua.deploy-operation.recovery",
            Action: "recover",
            Target: "prod-api",
            Environments: ["prod"],
            DesiredState: "recover:op-recover-1",
            IdempotencyKey: "honua-devops:recovery:op-recover-1",
            PolicyGate: "protected-recovery",
            AuthorizationDryRun: false,
            Actor: "operator@honua.io");

    private static DateTimeOffset FixedNow() => new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static MutableTimeProvider FixedTimeProvider() => new(FixedNow());

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
