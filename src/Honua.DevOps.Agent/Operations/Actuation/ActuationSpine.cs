using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.Actuation;

// The single DevOps actuation seam (issue #153).
//
//   typed action request -> durable operation identity -> trusted caller/target context
//     -> policy decision -> approval receipt when required -> typed actuator
//     -> backend mutation -> verification/evidence -> terminal result
//
// Enforcement is structural rather than advisory. The two grant types below are nested here
// with private constructors and can only be issued by presenting `IssuanceSeal` — a private
// static of this class that no code outside it can reach. A hand-rolled grant therefore
// throws instead of authorizing anything. Every mutating BackendGateway method requires a
// grant and re-checks that the grant was issued for that exact route, so "mutate before the
// durable operation and its authorization exist" has no path through the type system.
// BackendMutationCatalogTests closes the remaining gap by failing when a new gateway method
// appears that is neither catalogued as non-mutating nor gated on a grant.
//
// Local execution policy is a SAFETY CEILING. It can refuse or demand more governance; it
// can never grant server or cloud authority. A caller boolean, an execution-tier string, an
// approval-mode string, or model intent never satisfies the gate on its own.
internal sealed class ActuationSpine
{
    // At-most-once ledger keyed by idempotency key + route. A retry or a concurrent delivery
    // of the same sealed request observes the original claim instead of minting a second
    // grant for the same write, so one session issues at most one mutation per operation.
    //
    // Across a restart the ledger is empty by construction; at-most-once there comes from the
    // deterministic idempotency key this spine seals and the create call carries, which the
    // control plane resolves back to the SAME durable operation. The two mechanisms are
    // complementary: the ledger covers in-flight duplication, the key covers process loss.
    private readonly ConcurrentDictionary<string, ActuationClaim> _claims = new(StringComparer.Ordinal);

    // Unforgeable issuance token. Nested types may read their containing type's private
    // members, so the grant factories below can check it; nothing outside ActuationSpine can
    // obtain a reference to it.
    private static readonly object IssuanceSeal = new();

    private readonly OperationRuntime _runtime;
    private readonly OperatorPolicyModel _policy;

    internal ActuationSpine(OperationRuntime runtime, OperatorPolicyModel policy)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    // ---------------------------------------------------------------------------------
    // Grants
    // ---------------------------------------------------------------------------------

    // Stage 1. Authorizes the identity-establishing write — creating the durable
    // deploy-control operation with submitImmediately=false. It seals the target, the
    // request digest, the idempotency key, the policy decision, and the actor BEFORE the
    // operation exists, so the later mutation can be bound back to exactly this request.
    internal sealed class OperationGrant
    {
        private OperationGrant(
            string actuatorId,
            string action,
            string target,
            string requestDigest,
            string idempotencyKey,
            string policyGate,
            string actor,
            BackendMutation lifecycleEntry,
            GitOpsActuationDecision decision)
        {
            ActuatorId = actuatorId;
            Mutation = lifecycleEntry;
            Action = action;
            Target = target;
            RequestDigest = requestDigest;
            IdempotencyKey = idempotencyKey;
            PolicyGate = policyGate;
            Actor = actor;
            Decision = decision;
        }

        internal string ActuatorId { get; }

        internal string Action { get; }

        internal string Target { get; }

        // SHA-256 over the sealed desired-state/request payload. A mutation grant may only
        // be derived from an operation grant carrying the same digest.
        internal string RequestDigest { get; }

        internal string IdempotencyKey { get; }

        internal string PolicyGate { get; }

        internal string Actor { get; }

        internal GitOpsActuationDecision Decision { get; }

        // The lifecycle-entry route this grant authorizes: creating the durable deploy
        // operation, the metadata-release operation, or the server-owned proposal. All three
        // record a governed request and execute nothing against the target.
        internal BackendMutation Mutation { get; }

        // Symmetric with MutationGrant.EnsureAuthorizes: an operation grant is authority for
        // the identity-establishing create and nothing else.
        internal void EnsureAuthorizes(BackendMutation mutation)
        {
            if (Mutation != mutation)
            {
                throw new InvalidOperationException(
                    $"Operation grant for `{Action}` authorizes only `{Mutation}`, not `{mutation}`.");
            }
        }

        // Issued only by ActuationSpine: `seal` must be the spine's private IssuanceSeal,
        // which is unreachable from any other type.
        internal static OperationGrant Issue(
            object seal,
            string actuatorId,
            string action,
            string target,
            string requestDigest,
            string idempotencyKey,
            string policyGate,
            string actor,
            BackendMutation lifecycleEntry,
            GitOpsActuationDecision decision)
        {
            RequireSeal(seal);
            return new OperationGrant(
                actuatorId, action, target, requestDigest, idempotencyKey, policyGate, actor, lifecycleEntry, decision);
        }
    }

    // Stage 2. Authorizes ONE state-mutating backend route for ONE durable operation. It
    // cannot exist without a resolved server operation id and a satisfied approval, and it
    // carries the approval reference so the audit record joins the mutation to the decision
    // that permitted it.
    internal sealed class MutationGrant
    {
        private MutationGrant(
            OperationGrant origin,
            BackendMutation mutation,
            string operationId,
            ApprovalEvidence approval)
        {
            Origin = origin;
            Mutation = mutation;
            OperationId = operationId;
            Approval = approval;
        }

        internal OperationGrant Origin { get; }

        internal BackendMutation Mutation { get; }

        internal string OperationId { get; }

        internal ApprovalEvidence Approval { get; }

        internal string ActuatorId => Origin.ActuatorId;

        internal string Action => Origin.Action;

        internal string RequestDigest => Origin.RequestDigest;

        internal string IdempotencyKey => Origin.IdempotencyKey;

        internal static MutationGrant Issue(
            object seal,
            OperationGrant origin,
            BackendMutation mutation,
            string operationId,
            ApprovalEvidence approval)
        {
            RequireSeal(seal);
            return new MutationGrant(origin, mutation, operationId, approval);
        }

        // Called by every mutating BackendGateway method before it sends. A grant is write
        // authority for exactly one route on exactly one durable operation; presenting it
        // anywhere else is a programming error, not a recoverable condition.
        internal void EnsureAuthorizes(BackendMutation mutation)
        {
            if (Mutation != mutation)
            {
                throw new InvalidOperationException(
                    $"Actuation grant for `{Mutation}` on operation `{OperationId}` does not authorize `{mutation}`. " +
                    "Every mutating backend route needs its own grant from the durable actuation spine.");
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // Stage 1: seal the request and decide whether the lifecycle may be entered at all
    // ---------------------------------------------------------------------------------

    // Seals the request identity and records the policy decision. The grant it returns is
    // authority for the LIFECYCLE-ENTRY write only — creating the durable operation/proposal
    // record, which executes nothing against the target. That deliberately works in plan and
    // propose posture too: recording a governed proposal is what those modes are for.
    // Whether a state mutation may follow is a separate stage-2 decision below.
    internal ActuationAuthorization Authorize(ActuationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        GitOpsActuationDecision decision = GitOpsActuationDecision.Resolve(
            _runtime.ExecutionMode,
            _policy,
            request.AuthorizationDryRun,
            request.PolicyGate);

        // Fail closed before anything is written when the durable operation store cannot be
        // addressed. An operation id is never invented locally.
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            return ActuationAuthorization.Refused(
                ActuationOutcome.ContractUnavailable,
                "HONUA_DEVOPS_DEPLOY_TARGET_ID is not configured; cannot create a durable server operation.",
                "deploy-target-unconfigured",
                decision);
        }

        // Fail closed when the audit/receipt sink is unavailable: a mutation whose evidence
        // cannot be persisted is not permitted to start.
        if (string.IsNullOrWhiteSpace(_policy.AuditHookTarget))
        {
            return ActuationAuthorization.Refused(
                ActuationOutcome.ContractUnavailable,
                "No audit hook target is configured; a mutation whose evidence cannot be recorded is refused before it starts.",
                "audit-sink-unavailable",
                decision);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return ActuationAuthorization.Refused(
                ActuationOutcome.ContractUnavailable,
                "The request carries no idempotency key; a retry could not be proven to resume the original operation.",
                "idempotency-key-missing",
                decision);
        }

        OperationGrant grant = OperationGrant.Issue(
            IssuanceSeal,
            request.ActuatorId,
            request.Action,
            request.Target,
            ComputeDigest(request),
            request.IdempotencyKey,
            request.PolicyGate,
            request.Actor,
            request.LifecycleEntry,
            decision);

        return ActuationAuthorization.Granted(grant, decision);
    }

    // ---------------------------------------------------------------------------------
    // Bounded deployment recovery (issue #191)
    // ---------------------------------------------------------------------------------
    //
    // A DeploymentRecoveryGrant is a THIRD grant kind, distinct from OperationGrant/
    // MutationGrant above. It is minted once, at the moment a deployment is approved, and
    // seals the EXACT scope of compensation that approval authorized: the actor, tenant and
    // target it covers, the prior/candidate revision pair, the safety-policy digest that
    // governed the approval, an expiry, and which single compensation it permits. Recovery
    // execution later (RecoveryExecutor) re-verifies a request against this sealed scope
    // instead of trusting a fresh caller-supplied operationId/reason the way the generic,
    // model-invocable RollbackGitOpsOperationAsync tool does -- so "only the declared recovery
    // is preauthorized; generic model tools remain proposal-only and arbitrary rollback stays
    // gated" holds structurally, not just by convention.

    // Stage 1 for recovery: mint the sealed grant. Reuses Authorize's fail-closed checks
    // (target configured, audit sink configured, idempotency key present) and its
    // GitOpsActuationDecision, then additionally requires every recovery-specific field and an
    // expiry that is still in the future at issuance.
    internal bool AuthorizeRecoveryGrant(
        ActuationRequest request,
        string tenant,
        string priorRevision,
        string candidateRevision,
        string safetyPolicyDigest,
        DateTimeOffset expiresAtUtc,
        PermittedCompensation compensation,
        string? operationId,
        out DeploymentRecoveryGrant? grant,
        out string refusalReason,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        grant = null;

        ActuationAuthorization authorization = Authorize(request);
        if (!authorization.IsGranted)
        {
            refusalReason = authorization.Reason;
            return false;
        }

        if (!authorization.MayMutate)
        {
            refusalReason = authorization.Decision.Rationale;
            return false;
        }

        if (string.IsNullOrWhiteSpace(tenant)
            || string.IsNullOrWhiteSpace(priorRevision)
            || string.IsNullOrWhiteSpace(candidateRevision)
            || string.IsNullOrWhiteSpace(safetyPolicyDigest))
        {
            refusalReason = "A recovery grant requires a tenant, prior revision, candidate revision, and safety policy digest.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            refusalReason = "No durable operation id was returned by the control plane; a recovery grant cannot be bound to an invented id.";
            return false;
        }

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (expiresAtUtc <= now)
        {
            refusalReason = $"Recovery grant expiry `{expiresAtUtc:O}` must be after issuance time `{now:O}`.";
            return false;
        }

        grant = DeploymentRecoveryGrant.Issue(
            IssuanceSeal,
            request.Actor,
            tenant,
            request.Target,
            priorRevision,
            candidateRevision,
            safetyPolicyDigest,
            expiresAtUtc,
            compensation,
            request.IdempotencyKey,
            operationId.Trim(),
            authorization.Decision);
        refusalReason = string.Empty;
        return true;
    }

    // Stage 2 for recovery: re-verify a recovery request against the sealed grant (actor,
    // target, requested compensation, expiry, and a compare-and-set against the CURRENTLY
    // OBSERVED candidate revision so recovery never overwrites a newer approved intent), then
    // fall through to the same at-most-once ledger every other mutation uses so a restart/retry
    // observes the original claim instead of issuing a second compensation.
    internal bool TryAuthorizeRecovery(
        DeploymentRecoveryGrant grant,
        string requestedActor,
        string requestedTarget,
        string observedCandidateRevision,
        PermittedCompensation requestedCompensation,
        out MutationGrant? mutationGrant,
        out string refusalReason,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        mutationGrant = null;

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!grant.TryAuthorize(requestedActor, requestedTarget, observedCandidateRevision, requestedCompensation, now, out refusalReason))
        {
            return false;
        }

        OperationGrant origin = OperationGrant.Issue(
            IssuanceSeal,
            "honua.deploy-operation.recovery",
            "recover",
            grant.Target,
            ComputeRecoveryDigest(grant),
            grant.IdempotencyKey,
            "protected-recovery",
            grant.Actor,
            BackendMutation.DeployOperationCreate,
            grant.Decision);

        ApprovalEvidence approval = ApprovalEvidence.NotRequired(
            $"Bounded recovery grant for operation `{grant.OperationId}` was authorized at deployment-approval time " +
            $"(expires {grant.ExpiresAtUtc:O}); scope re-verified against actor, target, candidate revision, and expiry before compensation.");

        return TryAuthorizeMutation(origin, BackendMutation.DeployOperationRollback, grant.OperationId, approval, out mutationGrant, out refusalReason);
    }

    private static string ComputeRecoveryDigest(DeploymentRecoveryGrant grant)
    {
        string canonical = string.Join(
            "\n",
            grant.Actor,
            grant.Tenant,
            grant.Target,
            grant.PriorRevision,
            grant.CandidateRevision,
            grant.SafetyPolicyDigest,
            grant.Compensation.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // The bounded compensation(s) a DeploymentRecoveryGrant may authorize. Kept as its own enum
    // (rather than reusing BackendMutation) so a distinct compensation must be separately
    // declared and approved -- a grant issued for one can never be presented to authorize the
    // other; DeploymentRecoveryGrant.TryAuthorize refuses any requested compensation that does
    // not exactly match what was granted.
    internal enum PermittedCompensation
    {
        // Restore the target to PriorRevision through the deploy-control rollback route.
        RestorePriorRevision,

        // Record CandidateRevision as rejected without restoring traffic -- for a target where
        // the prior revision is no longer a safe restore point. Not yet wired to an actuator;
        // declared now so a grant scoped to it is structurally distinct from RestorePriorRevision.
        QuarantineCandidateOnly
    }

    // Immutable, sealed the same way OperationGrant/MutationGrant are: a private constructor
    // reachable only through Issue(seal, ...), so a hand-rolled grant cannot authorize anything.
    internal sealed class DeploymentRecoveryGrant
    {
        private DeploymentRecoveryGrant(
            string actor,
            string tenant,
            string target,
            string priorRevision,
            string candidateRevision,
            string safetyPolicyDigest,
            DateTimeOffset expiresAtUtc,
            PermittedCompensation compensation,
            string idempotencyKey,
            string operationId,
            GitOpsActuationDecision decision)
        {
            Actor = actor;
            Tenant = tenant;
            Target = target;
            PriorRevision = priorRevision;
            CandidateRevision = candidateRevision;
            SafetyPolicyDigest = safetyPolicyDigest;
            ExpiresAtUtc = expiresAtUtc;
            Compensation = compensation;
            IdempotencyKey = idempotencyKey;
            OperationId = operationId;
            Decision = decision;
        }

        internal string Actor { get; }

        internal string Tenant { get; }

        internal string Target { get; }

        internal string PriorRevision { get; }

        internal string CandidateRevision { get; }

        internal string SafetyPolicyDigest { get; }

        internal DateTimeOffset ExpiresAtUtc { get; }

        internal PermittedCompensation Compensation { get; }

        internal string IdempotencyKey { get; }

        internal string OperationId { get; }

        internal GitOpsActuationDecision Decision { get; }

        // Every bound field must match exactly. A mismatch is a widening or misdirection
        // attempt (wrong actor/target, a broader compensation than declared, an expired grant,
        // or a candidate revision that has moved since approval) and is refused with a specific
        // machine-readable reason -- never silently narrowed or coerced to fit.
        internal bool TryAuthorize(
            string requestedActor,
            string requestedTarget,
            string observedCandidateRevision,
            PermittedCompensation requestedCompensation,
            DateTimeOffset now,
            out string refusalReason)
        {
            if (now > ExpiresAtUtc)
            {
                refusalReason = $"Recovery grant for operation `{OperationId}` expired at {ExpiresAtUtc:O} (now {now:O}).";
                return false;
            }

            if (!string.Equals(requestedActor, Actor, StringComparison.Ordinal))
            {
                refusalReason = $"Recovery grant for operation `{OperationId}` was issued to actor `{Actor}`, not `{requestedActor}`.";
                return false;
            }

            if (!string.Equals(requestedTarget, Target, StringComparison.Ordinal))
            {
                refusalReason = $"Recovery grant for operation `{OperationId}` was issued for target `{Target}`, not `{requestedTarget}`.";
                return false;
            }

            if (requestedCompensation != Compensation)
            {
                refusalReason = $"Recovery grant for operation `{OperationId}` authorizes `{Compensation}` only, not `{requestedCompensation}`.";
                return false;
            }

            // Compare-and-set: recovery may only compensate the exact candidate it was approved
            // against. If the operation now shows a different (newer) candidate revision, some
            // other approved intent has already superseded this grant.
            if (!string.Equals(observedCandidateRevision, CandidateRevision, StringComparison.Ordinal))
            {
                refusalReason =
                    $"Operation `{OperationId}` now shows candidate revision `{observedCandidateRevision}`, not the granted `{CandidateRevision}`; " +
                    "a newer approved intent has superseded this recovery grant.";
                return false;
            }

            refusalReason = string.Empty;
            return true;
        }

        internal static DeploymentRecoveryGrant Issue(
            object seal,
            string actor,
            string tenant,
            string target,
            string priorRevision,
            string candidateRevision,
            string safetyPolicyDigest,
            DateTimeOffset expiresAtUtc,
            PermittedCompensation compensation,
            string idempotencyKey,
            string operationId,
            GitOpsActuationDecision decision)
        {
            RequireSeal(seal);
            return new DeploymentRecoveryGrant(
                actor, tenant, target, priorRevision, candidateRevision, safetyPolicyDigest,
                expiresAtUtc, compensation, idempotencyKey, operationId, decision);
        }
    }

    // ---------------------------------------------------------------------------------
    // Stage 2: bind a resolved operation + verified approval to one mutating route
    // ---------------------------------------------------------------------------------

    // Converts the sealed operation grant into write authority for exactly one route.
    // Returns false — with a machine-readable reason — whenever the approval evidence does
    // not satisfy the gate, so the caller stops before any mutating call is issued.
    internal bool TryAuthorizeMutation(
        OperationGrant operationGrant,
        BackendMutation mutation,
        string? operationId,
        ApprovalEvidence approval,
        out MutationGrant? grant,
        out string refusalReason)
    {
        ArgumentNullException.ThrowIfNull(operationGrant);
        ArgumentNullException.ThrowIfNull(approval);
        grant = null;

        if (mutation == operationGrant.Mutation)
        {
            refusalReason =
                $"`{mutation}` is the identity-establishing lifecycle-entry write; it uses the operation grant, not a mutation grant.";
            return false;
        }

        // Safety ceiling: plan/propose posture, or a dry-run authorization, means no state
        // mutation may follow the lifecycle-entry record no matter what the caller asked for.
        if (!operationGrant.Decision.Mutating)
        {
            refusalReason = operationGrant.Decision.Rationale;
            return false;
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            refusalReason = "No durable operation id was returned by the control plane; no mutation may be bound to an invented id.";
            return false;
        }

        if (!approval.Satisfied)
        {
            refusalReason = approval.Reason;
            return false;
        }

        // At-most-once: the first claim for this idempotency key + route wins. A retry or a
        // concurrent delivery observes the original operation instead of issuing a second
        // mutation through a second actuator.
        string claimKey = $"{operationGrant.IdempotencyKey}|{mutation}";
        ActuationClaim claim = new(operationId.Trim(), operationGrant.ActuatorId, operationGrant.RequestDigest);
        ActuationClaim existing = _claims.GetOrAdd(claimKey, claim);
        if (!ReferenceEquals(existing, claim))
        {
            refusalReason =
                $"A {mutation} for idempotency key `{operationGrant.IdempotencyKey}` was already claimed by actuator " +
                $"`{existing.ActuatorId}` on operation `{existing.OperationId}`; observe that operation instead of issuing a second mutation.";
            return false;
        }

        grant = MutationGrant.Issue(IssuanceSeal, operationGrant, mutation, operationId.Trim(), approval);
        refusalReason = string.Empty;
        return true;
    }

    // Deterministic SHA-256 over the sealed request identity. The same logical request
    // always produces the same digest, which is what lets a retry prove it is the same work.
    internal static string ComputeDigest(ActuationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string canonical = string.Join(
            "\n",
            request.ActuatorId,
            request.Action,
            request.Target,
            string.Join(",", request.Environments),
            request.DesiredState,
            request.IdempotencyKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void RequireSeal(object seal)
    {
        if (!ReferenceEquals(seal, IssuanceSeal))
        {
            throw new InvalidOperationException(
                "Actuation grants may only be issued by ActuationSpine. A mutating backend call must be authorized by " +
                "the durable actuation spine (typed request -> durable operation -> policy decision -> approval), " +
                "never by a locally constructed grant.");
        }
    }

    private sealed record ActuationClaim(string OperationId, string ActuatorId, string RequestDigest);
}

// A typed write request, sealed before any mutating call. `DesiredState` is the caller's
// canonical request payload (revision + parameters, or the serialized manifest) and is what
// the digest is taken over.
internal sealed record ActuationRequest(
    string ActuatorId,
    string Action,
    string Target,
    IReadOnlyList<string> Environments,
    string DesiredState,
    string IdempotencyKey,
    string PolicyGate,
    bool AuthorizationDryRun,
    string Actor,
    BackendMutation LifecycleEntry = BackendMutation.DeployOperationCreate);

// Why a mutation is permitted. There are exactly two acceptable sources, and neither is a
// caller flag: the authoritative control plane did not park the operation for approval, or
// a registered deterministic direct-execution policy result allows it.
internal sealed record ApprovalEvidence(
    bool Satisfied,
    string Kind,
    string? ReceiptId,
    string Reason)
{
    internal static ApprovalEvidence NotRequired(string reason)
        => new(Satisfied: true, Kind: "not-required", ReceiptId: null, Reason: reason);

    // The control plane's own decision: the operation is not parked at AwaitingApproval and
    // carries no blocking reasons. The server operation id IS the approval reference.
    internal static ApprovalEvidence FromControlPlane(
        string operationId,
        bool awaitingApproval,
        IReadOnlyList<string> blockingReasons)
    {
        if (awaitingApproval)
        {
            return new(
                Satisfied: false,
                Kind: "control-plane",
                ReceiptId: operationId,
                Reason: "The control plane parked the operation at AwaitingApproval; it requires explicit approval before execution.");
        }

        if (blockingReasons.Count > 0)
        {
            return new(
                Satisfied: false,
                Kind: "control-plane",
                ReceiptId: operationId,
                Reason: $"The control plane reported blocking reasons: {string.Join("; ", blockingReasons)}.");
        }

        return new(
            Satisfied: true,
            Kind: "control-plane",
            ReceiptId: operationId,
            Reason: "The control plane did not require approval for this operation.");
    }

    // A registered deterministic direct-execution policy result (direct-allowed /
    // break-glass-only in the break-glass tier). This is a pre-authorization recorded in the
    // operator policy, not a per-call boolean.
    internal static ApprovalEvidence FromDirectExecutionPolicy(GitOpsActuationDecision decision)
        => decision.MayAutoSubmit
            ? new(
                Satisfied: true,
                Kind: "direct-execution-policy",
                ReceiptId: $"policy:{decision.ApprovalMode}",
                Reason: $"Approval mode `{decision.ApprovalMode}` is a registered direct-execution policy result.")
            : new(
                Satisfied: false,
                Kind: "direct-execution-policy",
                ReceiptId: null,
                Reason: $"Approval mode `{decision.ApprovalMode}` requires external approval before any mutation.");

    // Both gates must hold: the policy ceiling AND the control plane's own decision.
    internal ApprovalEvidence And(ApprovalEvidence other)
        => Satisfied ? other : this;
}

// Outcome of ActuationSpine.Authorize.
internal sealed record ActuationAuthorization(
    bool IsGranted,
    string Outcome,
    string Reason,
    string? BlockingReason,
    GitOpsActuationDecision Decision,
    ActuationSpine.OperationGrant? Grant)
{
    internal static ActuationAuthorization Granted(ActuationSpine.OperationGrant grant, GitOpsActuationDecision decision)
        => new(true, ActuationOutcome.InProgress, decision.Rationale, null, decision, grant);

    internal static ActuationAuthorization NotMutating(GitOpsActuationDecision decision)
        => new(false, ActuationOutcome.PlanOnly, decision.Rationale, null, decision, null);

    // True when the sealed policy decision permits a state mutation to follow the
    // lifecycle-entry record. Callers surface plan-only from this without calling stage 2.
    internal bool MayMutate => IsGranted && Decision.Mutating;

    internal static ActuationAuthorization Refused(
        string outcome,
        string reason,
        string blockingReason,
        GitOpsActuationDecision decision)
        => new(false, outcome, reason, blockingReason, decision, null);
}
