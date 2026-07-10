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

    private const string RollbackEnableVariable = OperationRuntime.RollbackEnabledVariable;
    private const string CrossEnvEnableVariable = OperationRuntime.CrossEnvironmentPromotionEnabledVariable;

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
}
