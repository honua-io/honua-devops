using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.Audit;
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
    private readonly IAuditSink? _auditSink;

    internal ActuationSpine(OperationRuntime runtime, OperatorPolicyModel policy, IAuditSink? auditSink = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _auditSink = auditSink;
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
        if (!DurableAuditGate.TryProbe(_policy.AuditHookTarget, _auditSink, out string auditFailure))
        {
            return ActuationAuthorization.Refused(
                ActuationOutcome.ContractUnavailable,
                $"Durable audit evidence is unavailable: {auditFailure}. Mutation refused before it starts.",
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
