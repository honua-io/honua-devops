using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal static class ReleaseOrchestrationPlanner
{
    internal static ReleaseOrchestrationPlan Build(
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows,
        IReadOnlyList<string> targetEnvironments,
        string requestedAction,
        bool dryRun,
        string policyGate)
    {
        IReadOnlyList<RuntimeAdapterCapability> capabilities = adapterWorkflows
            .Select(workflow => workflow.Capability)
            .ToArray();

        bool targetsProd = targetEnvironments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        bool multiEnvironment = targetEnvironments.Count > 1;
        bool requiresOutOfBandMigrations = capabilities.Any(capability => capability.RequiresOutOfBandMigrations);
        string strategy = BuildStrategy(capabilities);
        string migrationMode = requiresOutOfBandMigrations
            ? "out-of-band-migration"
            : "compatibility-reviewed-migration";
        string promotionMode = targetsProd || requestedAction == "promote" || multiEnvironment
            ? "gated-promotion"
            : "single-environment-rollout";
        ReleasePromotionPolicy promotionPolicy = promotionMode == "gated-promotion"
            ? new ReleasePromotionPolicy(
                Gate: "promotion-approval",
                RequiredEvidence:
                [
                    "approval-record",
                    "lower-env-evidence",
                    "smoke-contract",
                    "slo-gate-evidence"
                ],
                Blockers:
                [
                    "failed-smoke-contract",
                    "failed-slo-gate",
                    "missing-approval-record",
                    "rollback-triggered"
                ])
            : new ReleasePromotionPolicy(
                Gate: "single-environment-auto-advance",
                RequiredEvidence:
                [
                    "release-evidence",
                    "smoke-contract"
                ],
                Blockers:
                [
                    "failed-rollout-verification",
                    "rollback-triggered"
                ]);
        ReleaseRollbackPolicy rollbackPolicy = new(
            FallbackMode: "known-good-revision-or-checkpoint",
            Triggers:
            [
                "failed-migration",
                "failed-smoke-contract",
                "failed-slo-gate",
                "operator-stop"
            ],
            RequiredEvidence:
            [
                "rollback-evidence",
                "rollback-classification",
                "known-good-revision",
                "backup-evidence"
            ]);

        List<ReleaseStagePlan> stages =
        [
            new(
                ReleaseStageKind.Preflight,
                "Validate diff scope, adapter target fit, and rollback prerequisites before any change advances.",
                "always",
                policyGate,
                [
                    "manifest-diff",
                    "target-validation",
                    "rollback-classification"
                ],
                adapterWorkflows.Select(workflow => workflow.Validate.SuggestedCommands.First()).ToArray()),
            new(
                ReleaseStageKind.Backup,
                "Capture an application and infrastructure recovery checkpoint before rollout.",
                dryRun ? "plan-only" : "before-execute",
                "backup-checkpoint",
                [
                    "backup-evidence",
                    "known-good-revision"
                ],
                [
                    "capture backup snapshot for app, config, and schema recovery",
                    "record known-good release artifact and environment checkpoint"
                ]),
            new(
                ReleaseStageKind.Migration,
                requiresOutOfBandMigrations
                    ? "Run migrations out of band before traffic moves to the new revision."
                    : "Confirm migration compatibility before rollout and avoid hidden startup migration behavior.",
                "before-rollout",
                migrationMode,
                [
                    "migration-plan",
                    "schema-compatibility-review"
                ],
                [
                    requiresOutOfBandMigrations
                        ? "run explicit migration job or control-plane migration step before rollout"
                        : "confirm expand-contract compatibility before rollout"
                ]),
            new(
                ReleaseStageKind.Rollout,
                "Advance the release through adapter-specific rollout steps with explicit stop gates.",
                dryRun ? "plan-only" : "after-migration",
                policyGate,
                [
                    "release-evidence"
                ],
                adapterWorkflows.Select(workflow => workflow.ApplyRelease.SuggestedCommands.First()).ToArray()),
            new(
                ReleaseStageKind.Smoke,
                "Verify post-rollout health with the shared smoke contract before promotion.",
                "after-rollout",
                "smoke-contract",
                [
                    "smoke-contract"
                ],
                [
                    "HONUA_SMOKE_BASE_URL=https://<endpoint> ./scripts/smoke-contract.sh"
                ]),
            new(
                ReleaseStageKind.SloWatch,
                "Hold the release in observation long enough to validate SLO gate health before promotion.",
                "after-smoke",
                "slo-release-gate",
                [
                    "slo-gate-evidence"
                ],
                [
                    "HONUA_SLO_JSON_URL=https://<metrics-endpoint> SLO_ENABLE_BURN_RATE_CHECKS=true SLO_WATCH_AUTO_ROLLBACK=true SLO_WATCH_ROLLBACK_COMMAND=\"<rollback-command>\" ./scripts/slo-release-watch.sh"
                ])
        ];

        if (promotionMode == "gated-promotion")
        {
            stages.Add(new ReleaseStagePlan(
                ReleaseStageKind.Promote,
                "Promote only after smoke and SLO evidence are recorded for the current environment.",
                "if-promotion",
                "promotion-approval",
                [
                    "approval-record",
                    "lower-env-evidence"
                ],
                [
                    "advance to next environment only when smoke and SLO evidence pass"
                ]));
        }

        stages.Add(new ReleaseStagePlan(
            ReleaseStageKind.Rollback,
            "Rollback on failed smoke, failed SLO gate, or explicit operator stop condition.",
            "on-failure",
            "rollback-trigger",
            [
                "rollback-evidence",
                "rollback-classification"
            ],
            adapterWorkflows.Select(workflow => workflow.Rollback.SuggestedCommands.First()).ToArray()));

        return new ReleaseOrchestrationPlan(
            Strategy: strategy,
            MigrationMode: migrationMode,
            PromotionMode: promotionMode,
            PromotionPolicy: promotionPolicy,
            RollbackPolicy: rollbackPolicy,
            Stages: stages,
            RollbackClasses:
            [
                "infrastructure",
                "app-release",
                "service-config",
                "schema"
            ],
            RollbackSemantics:
            [
                new(
                    ChangeClass: "infrastructure",
                    Trigger: "infra plan divergence or rollout failure",
                    RecoveryPath: "re-apply last known-good Terraform state and runtime adapter rollback handle",
                    EvidenceRequirements: ["backup-evidence", "rollback-evidence", "known-good-revision"]),
                new(
                    ChangeClass: "app-release",
                    Trigger: "failed smoke contract or failed release verification",
                    RecoveryPath: "use adapter rollback handle to restore the last known-good artifact or release revision",
                    EvidenceRequirements: ["release-evidence", "rollback-evidence", "smoke-contract"]),
                new(
                    ChangeClass: "service-config",
                    Trigger: "service-state diff or metadata drift after rollout",
                    RecoveryPath: "reconcile ServiceBundle back to exported desired state before retry",
                    EvidenceRequirements: ["service-state-export", "metadata-drift-read", "rollback-evidence"]),
                new(
                    ChangeClass: "schema",
                    Trigger: "migration incompatibility or post-rollout data access regression",
                    RecoveryPath: "hold promotion, run explicit fallback migration or restore compatible schema checkpoint",
                    EvidenceRequirements: ["migration-plan", "schema-compatibility-review", "rollback-evidence"])
            ],
            PromotionSequence: BuildPromotionSequence(targetEnvironments, promotionPolicy.Gate));
    }

    internal static IReadOnlyList<string> FlattenEvidenceRequirements(ReleaseOrchestrationPlan plan)
    {
        return plan.Stages
            .SelectMany(stage => stage.EvidenceRequirements)
            .Concat(plan.PromotionSequence.SelectMany(step => step.RequiredEvidence))
            .Concat(plan.RollbackSemantics.SelectMany(semantics => semantics.EvidenceRequirements))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildStrategy(IReadOnlyList<RuntimeAdapterCapability> capabilities)
    {
        string[] families = capabilities
            .Select(capability => capability.Family)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return families.Length switch
        {
            0 => "no-runtime-strategy",
            1 => $"{families[0]}-release-strategy",
            _ => $"multi-runtime:{string.Join("+", families)}"
        };
    }

    private static IReadOnlyList<ReleasePromotionStep> BuildPromotionSequence(
        IReadOnlyList<string> targetEnvironments,
        string promotionGate)
    {
        List<ReleasePromotionStep> steps = [];
        string? previousEnvironment = null;

        foreach (string environment in targetEnvironments)
        {
            List<string> requiredEvidence =
            [
                "release-evidence",
                "smoke-contract"
            ];
            if (previousEnvironment is not null)
            {
                requiredEvidence.Add("slo-gate-evidence");
            }

            if (environment.Equals("prod", StringComparison.OrdinalIgnoreCase))
            {
                requiredEvidence.Add("approval-record");
                requiredEvidence.Add("lower-env-evidence");
            }

            steps.Add(new ReleasePromotionStep(
                SourceEnvironment: previousEnvironment,
                TargetEnvironment: environment,
                Gate: previousEnvironment is null ? "initial-rollout" : promotionGate,
                RequiredEvidence: requiredEvidence.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                SuggestedCommands:
                [
                    previousEnvironment is null
                        ? $"roll out `{environment}` after preflight, backup, migration, and smoke gates pass"
                        : $"promote validated revision from `{previousEnvironment}` to `{environment}` only after evidence is recorded"
                ]));
            previousEnvironment = environment;
        }

        return steps;
    }
}
