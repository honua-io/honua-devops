using System.Text.Json;

using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Release-posture capability gate for the MVP release. The operate model is a
/// single-environment deploy with health-gated <em>fix-forward</em> convergence
/// (roll-forward). Rollback / auto-rollback and cross-environment promotion are
/// retained in the codebase but treated as EXPERIMENTAL and OFF by default: they
/// are neither advertised to the AI operator nor actuated unless explicitly
/// enabled via their <c>HONUA_DEVOPS_EXPERIMENTAL_*</c> flags (see
/// <see cref="OperationRuntime"/>).
///
/// Kept as a small, transport-free static (mirroring
/// <see cref="WorkIntake.WorkIntakeEditionGate"/>) so it is unit-testable without
/// a live server and so the refusal shape matches the other gate responses.
/// </summary>
internal static class ReleaseCapabilityGate
{
    internal const string RollbackCapability = "gitops-rollback";
    internal const string CrossEnvironmentPromotionCapability = "cross-environment-promotion";
    internal const string ProtectedRecoveryCapability = "protected-deployment-recovery";

    private const string RollbackEnableVariable = OperationRuntime.RollbackEnabledVariable;
    private const string CrossEnvEnableVariable = OperationRuntime.CrossEnvironmentPromotionEnabledVariable;
    private const string ProtectedRecoveryEnableVariable = OperationRuntime.ProtectedRecoveryEnabledVariable;

    /// <summary>
    /// Returns the canonical rollback refusal when the release capability is disabled,
    /// otherwise <see langword="null"/>. Every rollback coordinator and executor uses
    /// this shared decision so alternate tool names cannot bypass the release posture.
    /// </summary>
    internal static OperationResponse? GetRollbackRefusal(OperationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.RollbackEnabled ? null : BuildRollbackDisabledResponse();
    }

    /// <summary>Refusal for the rollback surface when it is disabled for the release.</summary>
    internal static OperationResponse BuildRollbackDisabledResponse()
        => new(
            Status: "experimental-disabled",
            Summary: $"Capability `{RollbackCapability}` is experimental and disabled for this release.",
            Findings:
            [
                "This release ships a single-environment deploy with health-gated fix-forward (roll-forward) convergence.",
                "Rollback / auto-rollback is a post-release capability and is not advertised or actuated.",
                $"The rollback code is retained but gated; it stays off unless `{RollbackEnableVariable}` is explicitly enabled."
            ],
            Actions:
            [
                "Recover by rolling FORWARD: diagnose the failing signal, propose a corrected revision, and re-deploy through the governed create path (deploy_service_gitops / propose operation).",
                "Use honua_diagnose and get_devops_operation_status to verify health, then converge with a forward fix.",
                $"To evaluate the experimental rollback surface in a non-release context, set `{RollbackEnableVariable}=true`."
            ],
            ValidationChecks:
            [
                "Rollback stays disabled by default; only an explicit experimental opt-in enables it.",
                "The forward path (single-environment deploy) remains fully available."
            ],
            Risks:
            [
                "Enabling experimental rollback outside the release posture bypasses the fix-forward safety story."
            ]);

    /// <summary>
    /// Refusal for the cross-environment promotion surface when it is disabled for
    /// the release. Single-environment deploy stays available.
    /// </summary>
    internal static OperationResponse BuildCrossEnvironmentPromotionDisabledResponse(
        IReadOnlyList<string> requestedEnvironments,
        string action)
        => new(
            Status: "experimental-disabled",
            Summary: $"Capability `{CrossEnvironmentPromotionCapability}` is experimental and disabled for this release.",
            Findings:
            [
                $"Requested action `{action}` across environments [{string.Join(", ", requestedEnvironments)}] is a cross-environment promotion.",
                "This release supports single-environment deploy only; cross-environment promotion is a post-release capability.",
                $"The promotion code is retained but gated; it stays off unless `{CrossEnvEnableVariable}` is explicitly enabled."
            ],
            Actions:
            [
                "Deploy to a single target environment with a sync/apply action instead of promoting across environments.",
                "Repeat the single-environment deploy per environment through the governed create path if multiple environments need the change.",
                $"To evaluate the experimental cross-environment promotion surface in a non-release context, set `{CrossEnvEnableVariable}=true`."
            ],
            ValidationChecks:
            [
                "Cross-environment promotion stays disabled by default; only an explicit experimental opt-in enables it.",
                "Single-environment sync/apply deploy remains fully available."
            ],
            Risks:
            [
                "Enabling experimental cross-environment promotion outside the release posture ships an unsupported multi-environment path."
            ]);

    /// <summary>
    /// Returns the canonical protected-recovery refusal when the capability is disabled,
    /// otherwise <see langword="null"/>. Distinct from <see cref="GetRollbackRefusal"/>: this
    /// gates only the bounded, declared-at-approval-time compensation
    /// (<c>DeploymentRecoveryGrant</c>/<c>RecoveryExecutor</c>, honua-devops#191), never the
    /// free-form model-invoked rollback tool.
    /// </summary>
    internal static OperationResponse? GetProtectedRecoveryRefusal(OperationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.ProtectedRecoveryEnabled ? null : BuildProtectedRecoveryDisabledResponse();
    }

    // An environment flag is only a local safety ceiling. Require the server's durable
    // observation scope before the retained executor can request compensation.
    internal static string? GetProtectedRecoveryScopeRefusal(
        ActuationSpine.DeploymentRecoveryGrant grant, JsonElement operation)
    {
        if (!string.Equals(DeployOperationReader.ReadStatus(operation), "Reconciling", StringComparison.OrdinalIgnoreCase)
            || DeployOperationReader.ReadBlockingReasons(operation).Count != 0
            || DeployOperationReader.ReadProtectionPhase(operation) is not ("observing" or "protected")
            || !string.Equals(DeployOperationReader.ReadPriorRevision(operation), grant.PriorRevision, StringComparison.Ordinal)
            || !string.Equals(DeployOperationReader.ReadProtectionPolicyDigest(operation), grant.SafetyPolicyDigest, StringComparison.Ordinal)
            || !operation.TryGetProperty("protection", out JsonElement protection)
            || !protection.TryGetProperty("candidateRevision", out JsonElement candidate)
            || candidate.ValueKind != JsonValueKind.String
            || !string.Equals(candidate.GetString(), grant.CandidateRevision, StringComparison.Ordinal)
            || !protection.TryGetProperty("observationDeadline", out JsonElement deadline)
            || deadline.ValueKind != JsonValueKind.String
            || !deadline.TryGetDateTimeOffset(out DateTimeOffset expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow)
        {
            return "The server has no matching active protection scope for this prior revision and safety policy.";
        }

        return null;
    }

    // Terminal/recovering observations may no longer have an active protection phase, but
    // they must still carry the immutable scope that this grant was approved for.
    internal static string? GetProtectedRecoveryObservationScopeRefusal(
        ActuationSpine.DeploymentRecoveryGrant grant, JsonElement operation)
    {
        // A settled server RolledBack clears the protection window (both the reconciler and
        // the manual rollback path call WithoutProtection), so its absence is server truth, not
        // a missing scope. The caller has already matched operation, target and candidate, and
        // this restart only observes: it never issues a compensation.
        if (DeployOperationReader.IsRolledBack(DeployOperationReader.ReadStatus(operation))
            && (!operation.TryGetProperty("protection", out JsonElement settled) || settled.ValueKind == JsonValueKind.Null))
        {
            return null;
        }

        if (!string.Equals(DeployOperationReader.ReadPriorRevision(operation), grant.PriorRevision, StringComparison.Ordinal)
            || !string.Equals(DeployOperationReader.ReadProtectionPolicyDigest(operation), grant.SafetyPolicyDigest, StringComparison.Ordinal)
            || !operation.TryGetProperty("protection", out JsonElement protection)
            || !protection.TryGetProperty("candidateRevision", out JsonElement candidate)
            || candidate.ValueKind != JsonValueKind.String
            || !string.Equals(candidate.GetString(), grant.CandidateRevision, StringComparison.Ordinal))
        {
            return "The server recovery observation does not match this grant's immutable protection scope.";
        }

        return null;
    }

    // Restored intent and candidate quarantine must survive a restart, or the next reconcile
    // could resurrect the rejected candidate. No durable ledger means no protected recovery.
    internal static string? GetProtectedRecoveryIntentLedgerRefusal(IDesiredIntentLedger? ledger)
        => ledger is null
            ? "Protected recovery needs a durable desired-intent ledger: set `HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///path/to/audit.jsonl` so restored intent and candidate quarantine survive a restart."
            : null;

    // Server-owned recovery (the reconciler's own rollback signal, or a rollback that settled
    // while no DevOps process watched) is folded into desired intent only for the exact
    // operation, target and candidate that approval recorded.
    internal static string? GetServerRecoveryIdentityRefusal(
        DesiredIntentRecord approved, string? operationId, string? targetId, string? candidateRevision)
        => string.Equals(operationId, approved.OperationId, StringComparison.Ordinal)
            && string.Equals(targetId, approved.Target, StringComparison.Ordinal)
            && string.Equals(candidateRevision, approved.DesiredRevision, StringComparison.Ordinal)
                ? null
                : $"The server reports RolledBack for `{operationId ?? "unknown"}` (target `{targetId ?? "unknown"}`, candidate " +
                  $"`{candidateRevision ?? "unknown"}`), which does not match approved intent `{approved.DesiredRevision}` from " +
                  $"operation `{approved.OperationId}` for `{approved.Target}`; desired intent was not changed.";

    // A revision rejected by a recorded recovery stays quarantined for its target; the operate
    // path is a corrected forward revision, never a reconcile back onto the rejected candidate.
    internal static string? GetQuarantinedRevisionRefusal(DesiredIntentRecord? latest, string desiredRevision)
        => latest is not null && latest.IsQuarantined(desiredRevision)
            ? $"Revision `{desiredRevision}` was rejected and quarantined for target `{latest.Target}` (desired-intent version {latest.Version}, " +
              $"operation `{latest.OperationId}`); propose a corrected revision instead of reconciling it again."
            : null;

    /// <summary>Refusal for the bounded protected-recovery surface when it is disabled.</summary>
    internal static OperationResponse BuildProtectedRecoveryDisabledResponse()
        => new(
            Status: "experimental-disabled",
            Summary: $"Capability `{ProtectedRecoveryCapability}` is not qualified/enabled for this target.",
            Findings:
            [
                "Bounded deployment recovery compensates only the exact prior/candidate revision pair declared and approved at deployment time.",
                $"The recovery code is retained but gated; it stays off unless `{ProtectedRecoveryEnableVariable}` is explicitly enabled for a qualified target.",
                $"This is independent of `{RollbackEnableVariable}`: enabling one does not enable the other."
            ],
            Actions:
            [
                "Recover by rolling FORWARD through the governed create path until this target is qualified for bounded recovery.",
                "Keep protected recovery disabled until the server's scoped recovery request, atomic target-intent check and installed recovery are qualified (see docs/protected-deployment-recovery.md).",
                "Protected recovery also requires a file-backed `HONUA_DEVOPS_AUDIT_HOOK_TARGET`: its desired-intent ledger records restored intent and candidate quarantine for the next reconcile.",
                "When enabled, a deploy the server recovers on its own is recorded too: the candidate is quarantined, and `Previous version restored` is reported only when the server exposed the prior revision."
            ],
            ValidationChecks:
            [
                "Bounded recovery stays disabled by default; only an explicit opt-in per qualified target enables it.",
                "The forward path (single-environment deploy) remains fully available."
            ],
            Risks:
            [
                "Enabling bounded recovery for a target that has not been certified end-to-end (honua-release#321) ships an unverified safety claim."
            ]);
}
