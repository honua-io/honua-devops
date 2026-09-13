namespace Honua.DevOps.Agent.Operations.Actuation;

// The authoritative outcome vocabulary for anything that can write (issues #151/#153).
//
// These tokens describe what the ACTUATOR authority reported, not what a caller asked for.
// `autoApply`, an execution tier, an approval-mode string, a `confirmed=true` flag, or a
// natural-language intent can authorize an available actuator; none of them can create one,
// and none of them may promote a response into Executed.
internal static class ActuationOutcome
{
    // No typed actuator is registered for the requested action. Zero backend calls ran.
    // This is the honest answer for an unknown runbook or an unimplemented remediation.
    internal const string UnsupportedAction = "unsupported-action";

    // A typed actuator exists, but no mutation was requested or permitted. Nothing ran
    // against the backend beyond read-only planning.
    internal const string PlanOnly = "plan-only";

    // A registered READ-ONLY actuator ran and returned backend state. Real work happened,
    // but nothing mutated — so this is never reported as executed or applied.
    internal const string Observed = "observed";

    // A durable operation exists and is parked for an external approver. Nothing executed.
    internal const string AwaitingApproval = "awaiting-approval";

    // The authority refused the mutation (server approval gate, local classification gate).
    internal const string ApprovalRequired = "approval-required";

    // Submitted; the authority has not yet reached a terminal state. Not a failure.
    internal const string InProgress = "in-progress";

    // Terminal success from the actuator authority WITH a receipt and a successful
    // mutating backend step. The only outcome that may be reported as executed/applied.
    internal const string Executed = "executed";

    // Terminal non-success from the actuator authority.
    internal const string Failed = "failed";

    // Terminal rollback reported by the authority.
    internal const string RolledBack = "rolled-back";

    // A mutating call was issued but its result could not be established (evidence write
    // failed, response unreadable, transport ambiguity). Never report success from here.
    internal const string Indeterminate = "indeterminate";

    // The backend contract was unavailable: no operation id returned, target unconfigured,
    // or a backend error before anything could mutate.
    internal const string ContractUnavailable = "contract-unavailable";

    // A backend call failed outright.
    internal const string BackendError = "backend-error";

    // The capability is experimental and disabled. Nothing was read or mutated.
    internal const string ExperimentalDisabled = "experimental-disabled";

    // Outcomes that a response may present as executed/applied. Deliberately a single value.
    internal static readonly IReadOnlySet<string> SuccessOutcomes =
        new HashSet<string>(StringComparer.Ordinal) { Executed };

    // Outcomes that guarantee no state was mutated.
    internal static readonly IReadOnlySet<string> NonMutatingOutcomes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            UnsupportedAction,
            PlanOnly,
            Observed,
            AwaitingApproval,
            ApprovalRequired,
            ContractUnavailable,
            ExperimentalDisabled
        };
}

// The durable receipt an actuation must carry before any response may claim it executed.
// `ReceiptId` is the authoritative server operation/action identity — DevOps never invents
// one, so a missing upstream id stays null and blocks the claim that depends on it.
internal sealed record ActuationReceipt(
    string ActuatorId,
    string ReceiptId,
    string Source,
    string? ServerStatus);

// The single authoritative actuation result. Every write-capable tool derives its
// top-level status, its `Mutated` audit flag, and its backend steps from THIS object
// (issue #151); nothing re-infers success from a status string of its own.
internal sealed record ActuationResult(
    string ActuatorId,
    string Action,
    string Target,
    string Outcome,
    bool Mutated,
    ActuationReceipt? Receipt,
    string? OperationId,
    IReadOnlyList<OperationBackendStep> BackendSteps,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> BlockingReasons,
    string? IdempotencyKey = null)
{
    internal static ActuationResult Unsupported(string action, string target, string reason)
        => new(
            ActuatorId: "none",
            Action: action,
            Target: target,
            Outcome: ActuationOutcome.UnsupportedAction,
            Mutated: false,
            Receipt: null,
            OperationId: null,
            BackendSteps: [],
            Findings: [reason],
            BlockingReasons: ["no-registered-actuator"]);

    // True only when the authority reported terminal success AND the evidence supports it:
    // a receipt exists and at least one mutating backend step succeeded. This is the
    // predicate the response guard enforces; it is not a re-derivation of the status.
    internal bool IsAuthoritativeSuccess
        => ActuationOutcome.SuccessOutcomes.Contains(Outcome)
            && Mutated
            && Receipt is not null
            && BackendSteps.Any(step => step.MutatesState && step.Success);
}
