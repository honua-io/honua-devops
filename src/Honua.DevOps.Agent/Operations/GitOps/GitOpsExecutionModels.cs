using System.Globalization;
using System.Text.Json;

using Honua.DevOps.Agent.Operations.ConsoleBridge;
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
    // The release capability is experimental and disabled. Nothing was read or mutated.
    internal const string ExperimentalDisabled = "experimental-disabled";

    // EXECUTION_MODE=plan (default): nothing was created, submitted, or rolled back.
    internal const string PlanOnly = "plan-only";

    // A durable server operation was created but, by policy/approval, was NOT submitted.
    // The operationId + evidence are surfaced for an external approver.
    internal const string AwaitingApproval = "awaiting-approval";

    // Submitted and polled to a successful terminal server status.
    internal const string Succeeded = "succeeded";

    // Submitted successfully, but the operation had NOT reached a terminal server status
    // within the poll budget. This is NOT a failure: the reconciler advances the operation
    // server-side and a real deploy/promotion routinely takes longer than the poll budget.
    // The operationId + last-observed ServerStatus are surfaced so the caller can keep
    // watching (e.g. via the approval/watch path) rather than treating it as failed.
    internal const string InProgress = "in-progress";

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

    // A mutating call was issued but its outcome could not be established: the operation may
    // or may not have taken effect, so this is reported as-is and never collapsed into
    // success or failure (issue #153). A successful-looking operation without an
    // authoritative actuator receipt bound to it is the same kind of ambiguity.
    internal const string Indeterminate = "indeterminate";
}

// Poll budget for SubmitAndPollAsync after a deploy/promotion is submitted. The reconciler
// advances the operation server-side, so a real deploy routinely takes far longer than a
// single poll cycle: the executor polls with capped exponential backoff up to a total
// timeout, and if the operation is still non-terminal at the deadline it reports
// `in-progress` (NOT failed). All three knobs are env-configurable, mirroring
// ApprovalWaiter's HONUA_DEVOPS_APPROVAL_TIMEOUT_SECONDS.
internal sealed record DeployPollPolicy(
    TimeSpan Timeout,
    TimeSpan InitialInterval,
    TimeSpan MaxInterval)
{
    internal const string TimeoutSecondsVariable = "HONUA_DEVOPS_DEPLOY_POLL_TIMEOUT_SECONDS";
    internal const string InitialIntervalMsVariable = "HONUA_DEVOPS_DEPLOY_POLL_INITIAL_INTERVAL_MS";
    internal const string MaxIntervalMsVariable = "HONUA_DEVOPS_DEPLOY_POLL_MAX_INTERVAL_MS";

    // Realistic defaults: poll for up to 5 minutes, starting at 1s and backing off to 15s.
    internal const int DefaultTimeoutSeconds = 300;
    internal const int DefaultInitialIntervalMs = 1000;
    internal const int DefaultMaxIntervalMs = 15000;

    internal static DeployPollPolicy Resolve()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(ReadPositiveInt(TimeoutSecondsVariable, DefaultTimeoutSeconds));
        TimeSpan initial = TimeSpan.FromMilliseconds(ReadPositiveInt(InitialIntervalMsVariable, DefaultInitialIntervalMs));
        TimeSpan max = TimeSpan.FromMilliseconds(ReadPositiveInt(MaxIntervalMsVariable, DefaultMaxIntervalMs));

        // The cap must never be below the initial interval; clamp to keep backoff monotonic.
        if (max < initial)
        {
            max = initial;
        }

        return new DeployPollPolicy(timeout, initial, max);
    }

    private static int ReadPositiveInt(string variable, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
        {
            throw new InvalidOperationException(
                $"Environment variable `{variable}` must be a positive integer.");
        }

        return value;
    }
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
    internal static string? ReadOperationId(JsonElement root)
        => ReadString(root, "operationId", "operation_id", "id");

    internal static string? ReadStatus(JsonElement root)
        => ReadString(root, "status", "state", "workflowStatus");

    // Status recognition delegates to the single canonical parser
    // (ServerOperationStatusParser) so the honua-server WorkflowOperationStatus vocabulary is
    // understood in exactly one place across the executors, the ApprovalWaiter, and the
    // Console bridge.
    internal static bool IsTerminal(string? status)
        => ServerOperationStatusParser.IsTerminal(status);

    internal static bool IsSuccess(string? status)
        => ServerOperationStatusParser.IsSuccess(status);

    internal static bool IsRolledBack(string? status)
        => ServerOperationStatusParser.IsRolledBack(status);

    // True when the server has parked the operation at AwaitingApproval, i.e. the deploy
    // plan required approval. The executor must NOT auto-submit such an operation under
    // pr-first; it surfaces the operationId for an external approver instead.
    internal static bool IsAwaitingApproval(string? status)
        => ServerOperationStatusParser.IsAwaitingApproval(status);

    internal static IReadOnlyList<string> ReadBlockingReasons(JsonElement root)
        => ReadStringArray(root, "blockingReasons", "blocking_reasons");

    internal static IReadOnlyDictionary<string, string> ReadParameters(JsonElement root)
    {
        if (root.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object)
        {
            return parameters.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static string? ReadActuatorReceiptId(JsonElement root)
    {
        string? direct = ReadString(root, "actuatorReceiptId", "actuator_receipt_id", "receiptId", "receipt_id");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (string propertyName in new[] { "actuatorReceipt", "actuator_receipt", "receipt" })
        {
            if (root.TryGetProperty(propertyName, out JsonElement receipt) && receipt.ValueKind == JsonValueKind.Object)
            {
                string? nested = ReadString(receipt, "receiptId", "receipt_id", "id");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    internal static string? ReadActuatorReceiptOperationId(JsonElement root)
    {
        foreach (string propertyName in new[] { "actuatorReceipt", "actuator_receipt", "receipt" })
        {
            if (root.TryGetProperty(propertyName, out JsonElement receipt) && receipt.ValueKind == JsonValueKind.Object)
            {
                string? nested = ReadString(receipt, "operationId", "operation_id");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return ReadString(root, "receiptOperationId", "receipt_operation_id");
    }

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

    // ---- Metadata-release detect helpers (Demo B safe-rollback loop) ----
    // The DevOps AI loop reads these from a metadata-release operation to confirm the deploy was
    // health-gated and rolled back (metadata + DB down-script) before it diagnoses + proposes a
    // resolve. They are tolerant of missing fields so a partial read still yields a useful signal.

    internal static bool IsManualInterventionRequired(string? status)
        => ServerOperationStatusParser.IsManualInterventionRequired(status);

    internal static string? ReadCurrentPhase(JsonElement root)
        => ReadString(root, "currentPhase", "current_phase");

    internal static string? ReadErrorMessage(JsonElement root)
        => ReadString(root, "errorMessage", "error_message", "error");

    internal static string? ReadMetadataReleaseStage(JsonElement root)
        => TryGetObject(root, "metadataRelease", out JsonElement metadataRelease)
            ? ReadString(metadataRelease, "currentStage", "stage")
            : null;

    // Returns true when the operation carries a recorded post-publish Smoke evidence ref — the
    // proof the health gate actually ran (pass or fail).
    internal static bool HasSmokeEvidence(JsonElement root)
    {
        if (!TryGetObject(root, "metadataRelease", out JsonElement metadataRelease)
            || metadataRelease.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in metadataRelease.EnumerateObject())
        {
            if (!string.Equals(property.Name, "evidenceRefs", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(property.Name, "evidence_refs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement evidence in property.Value.EnumerateArray())
            {
                string? kind = ReadString(evidence, "kind");
                if (kind is not null && Normalize(kind) == "smoke")
                {
                    return true;
                }
            }
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
