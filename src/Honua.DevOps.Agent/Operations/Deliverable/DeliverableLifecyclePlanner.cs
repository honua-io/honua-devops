using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;

namespace Honua.DevOps.Agent.Operations.Deliverable;

// Plan-only planner for the #77 deliverable lifecycle. It maps each lifecycle
// transition to a target environment, the gate governing it, the evidence that gate
// requires, and the minimum edition that unlocks it. It builds nothing and mutates
// nothing: no artifact is generated, no promotion is executed, no state is written.
//
// Edition boundary (founder decision):
//   - single-environment Draft -> Preview -> Approved is Pro,
//   - cross-environment Approved -> Published (to prod through deploy-control) is Enterprise.
// The planner records the required edition per transition; the toolkit enforces it.
//
// The Approved -> Published step does NOT define a new promotion engine: the caller
// supplies the ReleaseOrchestrationPlanner gated-promotion plan (the same engine
// PlanGitOpsEngineAsync uses) and the planner binds the transition to it, surfacing
// the engine's PromotionPolicy required-evidence verbatim.
internal static class DeliverableLifecyclePlanner
{
    internal const string ProEdition = "pro";
    internal const string EnterpriseEdition = "enterprise";

    // Required-evidence sets per gate. Draft->Preview is a lower-env single-environment
    // rollout; Preview->Approved is the steward-approval gate; Approved->Published reuses
    // the gated-promotion engine's required evidence (approval-record + lower-env-evidence
    // + smoke-contract + slo-gate-evidence).
    internal static readonly IReadOnlyList<string> PreviewEvidence =
    [
        "lower-env-rollout-evidence",
        "preview-link",
        "provenance-card"
    ];

    internal static readonly IReadOnlyList<string> ApprovalEvidence =
    [
        "preview-link",
        "scope-card",
        "approval-record"
    ];

    internal static readonly IReadOnlyList<string> PublishEvidence =
    [
        "approval-record",
        "lower-env-evidence",
        "smoke-contract",
        "slo-gate-evidence"
    ];

    // Build the lifecycle plan from the deliverable's current state to Published.
    //
    // lowerEnvironment   target for the Draft->Preview rollout (and the Approved env).
    // publishEnvironment target for the Approved->Published promotion (prod by default).
    // callerEdition      already-normalized edition; decides which steps are unlocked.
    // approvalTrigger    the abstracted Preview->Approved approval source (Console now).
    // promotionPlan      the ReleaseOrchestrationPlanner gated-promotion plan for the
    //                    Approved->Published step (null when the cross-env step is not
    //                    planned/unlocked, e.g. below Enterprise).
    // approvalOperationId stable id threading the governed approval action back.
    internal static DeliverableLifecyclePlan Build(
        Deliverable deliverable,
        string lowerEnvironment,
        string publishEnvironment,
        string callerEdition,
        IDeliverableApprovalTrigger approvalTrigger,
        ReleaseOrchestrationPlan? promotionPlan,
        string approvalOperationId)
    {
        ArgumentNullException.ThrowIfNull(deliverable);
        ArgumentNullException.ThrowIfNull(approvalTrigger);

        bool proUnlocked = EditionRank(callerEdition) >= EditionRank(ProEdition);
        bool enterpriseUnlocked = EditionRank(callerEdition) >= EditionRank(EnterpriseEdition);

        List<DeliverableTransition> transitions = [];

        // Draft -> Preview: lower-env single-environment rollout (Pro).
        if (deliverable.State == DeliverableLifecycleState.Draft)
        {
            transitions.Add(new DeliverableTransition(
                FromState: DeliverableLifecycleState.Draft,
                ToState: DeliverableLifecycleState.Preview,
                TargetEnvironment: lowerEnvironment,
                Gate: "lower-env-preview-rollout",
                RequiredEvidence: PreviewEvidence,
                RequiredEdition: ProEdition,
                PromotionMode: "single-environment-rollout",
                ApprovalSource: null,
                ApprovalAction: null,
                PromotionPlan: null));
        }

        // Preview -> Approved: steward-approval gate emitted as a governed SuggestedAction
        // via the abstracted approval trigger (Pro).
        if (deliverable.State <= DeliverableLifecycleState.Preview)
        {
            SuggestedAction approvalAction = approvalTrigger.BuildApprovalAction(deliverable, approvalOperationId);
            transitions.Add(new DeliverableTransition(
                FromState: DeliverableLifecycleState.Preview,
                ToState: DeliverableLifecycleState.Approved,
                TargetEnvironment: lowerEnvironment,
                Gate: "steward-approval",
                RequiredEvidence: ApprovalEvidence,
                RequiredEdition: ProEdition,
                PromotionMode: "approval-gate",
                ApprovalSource: approvalTrigger.Source,
                ApprovalAction: approvalAction,
                PromotionPlan: null));
        }

        // Approved -> Published: cross-environment gated-promotion to prod, reusing the
        // ReleaseOrchestrationPlanner gated-promotion plan (Enterprise).
        bool crossEnvPlanned = deliverable.State <= DeliverableLifecycleState.Approved;
        if (crossEnvPlanned)
        {
            IReadOnlyList<string> publishEvidence = promotionPlan is not null
                ? promotionPlan.PromotionPolicy.RequiredEvidence
                : PublishEvidence;
            transitions.Add(new DeliverableTransition(
                FromState: DeliverableLifecycleState.Approved,
                ToState: DeliverableLifecycleState.Published,
                TargetEnvironment: publishEnvironment,
                Gate: promotionPlan?.PromotionPolicy.Gate ?? "gated-promotion",
                RequiredEvidence: publishEvidence,
                RequiredEdition: EnterpriseEdition,
                PromotionMode: promotionPlan?.PromotionMode ?? "gated-promotion",
                ApprovalSource: null,
                ApprovalAction: null,
                PromotionPlan: promotionPlan));
        }

        _ = proUnlocked;

        return new DeliverableLifecyclePlan(
            DeliverableId: deliverable.DeliverableId,
            WorkItemId: deliverable.WorkItemId,
            Kind: deliverable.Kind,
            CurrentState: deliverable.State,
            LowerEnvironment: lowerEnvironment,
            PublishEnvironment: publishEnvironment,
            Transitions: transitions,
            CrossEnvironmentPromotionPlanned: crossEnvPlanned,
            CrossEnvironmentPromotionUnlocked: enterpriseUnlocked,
            CallerEdition: callerEdition);
    }

    // The governed Preview -> Approved approval action, if the plan includes that gate.
    internal static SuggestedAction? FindApprovalAction(DeliverableLifecyclePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Transitions
            .FirstOrDefault(transition => transition.ToState == DeliverableLifecycleState.Approved)
            ?.ApprovalAction;
    }

    // Flatten the evidence requirements across every planned transition for surfacing as
    // validation checks. Distinct + ordinal-insensitive, matching the release planner.
    internal static IReadOnlyList<string> FlattenEvidenceRequirements(DeliverableLifecyclePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Transitions
            .SelectMany(transition => transition.RequiredEvidence)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int EditionRank(string? edition)
    {
        return (edition?.Trim().ToLowerInvariant()) switch
        {
            "enterprise" => 3,
            "pro" => 2,
            "professional" => 2,
            _ => 1
        };
    }
}
