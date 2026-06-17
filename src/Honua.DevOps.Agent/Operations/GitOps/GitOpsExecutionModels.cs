using System.Text.Json;

using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Shared vocabulary and helpers for the GitOps actuation executors (sync / promote /
// rollback). The executors orchestrate THROUGH the honua-server deploy-control endpoints
// (preflight -> plan -> create operation -> [approval gate] -> submit -> poll) and never
// reimplement deployment execution. Everything in this file is default-safe: it only
// decides whether a mutation is permitted, it does not itself perform one.

// Outcome of a single actuation. Status values are stable, machine-readable tokens the
// toolkit copies onto the OperationResponse so the model/audit journal can reason about
// whether anything mutated and why.
internal static class GitOpsExecutionStatus
{
    // EXECUTION_MODE=plan (default): nothing was created, submitted, or rolled back.
    internal const string PlanOnly = "plan-only";

    // A durable server operation was created but, by policy/approval, was NOT submitted.
    // The operationId + evidence are surfaced for an external approver.
    internal const string AwaitingApproval = "awaiting-approval";

    // Submitted and polled to a successful terminal server status.
    internal const string Succeeded = "succeeded";

    // Submitted and polled to a failed/manual-intervention terminal server status.
    internal const string Failed = "failed";

    // Rollback completed (server reported RolledBack).
    internal const string RolledBack = "rolled-back";

    // A data-affecting / approval-gated mutation was refused (server returned a 403 from
    // the OperatorApprovalGate, or local policy declined to submit). Nothing mutated.
    internal const string ApprovalRequired = "approval-required";

    // The deploy-control contract was unavailable (no operationId returned, target
    // unconfigured, or a backend error). Nothing mutated; no operation id invented.
    internal const string ContractUnavailable = "contract-unavailable";
}

// How the executor was permitted to act, derived from EXECUTION_MODE + APPROVAL_MODE +
// execution tier. The decision is computed once, surfaced as evidence, and gates every
// mutating backend call. `Mutating` is the master switch: when false NOTHING is created,
// submitted, or rolled back regardless of the rest.
internal sealed record GitOpsActuationDecision(
    bool Mutating,
    bool MayAutoSubmit,
    string Mode,
    string ApprovalMode,
    string PolicyGate,
    string Rationale)
{
    // Default-safe decision: plan posture, no mutation, no auto-submit. Used whenever
    // EXECUTION_MODE is plan (the default) or the requested action is itself a dry-run.
    internal static GitOpsActuationDecision PlanOnly(string approvalMode, string policyGate, string rationale)
        => new(
            Mutating: false,
            MayAutoSubmit: false,
            Mode: "plan",
            ApprovalMode: approvalMode,
            PolicyGate: policyGate,
            Rationale: rationale);

    // Resolve the actuation decision from the runtime + policy + the per-request dry-run
    // verdict the toolkit already computed (AuthorizeDeployment.DryRun). This is the single
    // place that maps the safety posture onto "may I create / may I auto-submit".
    internal static GitOpsActuationDecision Resolve(
        ExecutionMode executionMode,
        OperatorPolicyModel policy,
        bool authorizationDryRun,
        string policyGate)
    {
        string approvalMode = policy.ApprovalMode.ToConfigValue();

        // Master safety invariant: plan mode (default) OR an authorization that resolved to
        // dry-run means NOTHING mutates. Same behavior as before this feature landed.
        if (executionMode != ExecutionMode.Execute || authorizationDryRun)
        {
            return PlanOnly(
                approvalMode,
                policyGate,
                executionMode != ExecutionMode.Execute
                    ? "EXECUTION_MODE=plan (default): create/submit/rollback are disabled; plan-only."
                    : "Execution tier resolved this request to dry-run; create/submit/rollback are disabled.");
        }

        // execute + write-enabled tier. Whether the executor may submit WITHOUT an external
        // approval depends on the approval mode:
        //   * pr-first (default): create the operation and PAUSE; never auto-submit.
        //   * direct-allowed: the operator pre-authorized direct execution; auto-submit ok.
        //   * break-glass-only: only the break-glass tier reaches here (AuthorizeDeployment
        //     enforces that), so auto-submit is permitted as the exceptional recovery path.
        bool mayAutoSubmit = policy.ApprovalMode switch
        {
            OperatorPolicy.ApprovalMode.DirectAllowed => true,
            OperatorPolicy.ApprovalMode.BreakGlassOnly => true,
            _ => false
        };

        string rationale = mayAutoSubmit
            ? $"EXECUTION_MODE=execute with approval-mode `{approvalMode}`: create and submit are permitted."
            : "EXECUTION_MODE=execute with approval-mode `pr-first`: create the operation and pause for external approval; never auto-submit.";

        return new GitOpsActuationDecision(
            Mutating: true,
            MayAutoSubmit: mayAutoSubmit,
            Mode: "execute",
            ApprovalMode: approvalMode,
            PolicyGate: policyGate,
            Rationale: rationale);
    }
}

// Tolerant readers for the honua-server DeployOperationResponse shape. Field names mirror
// the landed contract (operationId/status/blockingReasons, plus the nested target and the
// metadataRelease.rollbackPlan that carries the data-affecting classification).
internal static class DeployOperationReader
{
    private static readonly string[] TerminalStatuses =
    [
        "succeeded",
        "failed",
        "rolledback",
        "rolled-back",
        "manualinterventionrequired",
        "manual-intervention-required"
    ];

    private static readonly string[] SuccessStatuses = ["succeeded"];

    private static readonly string[] RolledBackStatuses = ["rolledback", "rolled-back"];

    internal static string? ReadOperationId(JsonElement root)
        => ReadString(root, "operationId", "operation_id", "id");

    internal static string? ReadStatus(JsonElement root)
        => ReadString(root, "status", "state", "workflowStatus");

    internal static bool IsTerminal(string? status)
        => status is not null && TerminalStatuses.Contains(Normalize(status));

    internal static bool IsSuccess(string? status)
        => status is not null && SuccessStatuses.Contains(Normalize(status));

    internal static bool IsRolledBack(string? status)
        => status is not null && RolledBackStatuses.Contains(Normalize(status));

    // True when the server has parked the operation at AwaitingApproval, i.e. the deploy
    // plan required approval. The executor must NOT auto-submit such an operation under
    // pr-first; it surfaces the operationId for an external approver instead.
    internal static bool IsAwaitingApproval(string? status)
        => Normalize(status ?? string.Empty) is "awaitingapproval" or "awaiting-approval";

    internal static IReadOnlyList<string> ReadBlockingReasons(JsonElement root)
        => ReadStringArray(root, "blockingReasons", "blocking_reasons");

    // metadataRelease.rollbackPlan.isDataAffecting drives the server's OperatorApprovalGate.
    // When absent the server defaults isDestructive=true, so we mirror that conservative
    // default: unknown => treat as data-affecting (requires explicit approval).
    internal static bool ReadRollbackIsDataAffecting(JsonElement root)
    {
        if (TryGetObject(root, "metadataRelease", out JsonElement metadataRelease)
            && TryGetObject(metadataRelease, "rollbackPlan", out JsonElement rollbackPlan))
        {
            if (TryReadBool(rollbackPlan, "isDataAffecting", out bool dataAffecting))
            {
                return dataAffecting;
            }

            // A MetadataOnly classification is the only non-data-affecting rollback class.
            string? rollbackClass = ReadString(rollbackPlan, "class", "rollbackClass");
            if (rollbackClass is not null)
            {
                return !Normalize(rollbackClass).Contains("metadataonly", StringComparison.Ordinal);
            }
        }

        // Conservative default, matching the server's `?? true`.
        return true;
    }

    internal static string? ReadRollbackClass(JsonElement root)
    {
        if (TryGetObject(root, "metadataRelease", out JsonElement metadataRelease)
            && TryGetObject(metadataRelease, "rollbackPlan", out JsonElement rollbackPlan))
        {
            return ReadString(rollbackPlan, "class", "rollbackClass");
        }

        return null;
    }

    internal static bool ReadRollbackRequiresExplicitApproval(JsonElement root)
    {
        if (TryGetObject(root, "metadataRelease", out JsonElement metadataRelease)
            && TryGetObject(metadataRelease, "rollbackPlan", out JsonElement rollbackPlan)
            && TryReadBool(rollbackPlan, "requiresExplicitApproval", out bool requires))
        {
            return requires;
        }

        return false;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    string? value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    return property.Value
                        .EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                }
            }
        }

        return [];
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.Value.GetBoolean();
                return true;
            }
        }

        return false;
    }
}

// The outcome the toolkit folds into its OperationResponse: the resulting status, the
// durable operationId (when one was created), the backend steps (each flagged
// mutatesState), and human-readable evidence/findings. Records-only, no behavior.
internal sealed record GitOpsExecutionResult(
    string Status,
    string? OperationId,
    string? ServerStatus,
    bool Mutated,
    GitOpsActuationDecision Decision,
    IReadOnlyList<OperationBackendStep> BackendSteps,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> BlockingReasons);
