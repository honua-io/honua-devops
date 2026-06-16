using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;

namespace Honua.DevOps.Agent.Operations.Deliverable;

// One ordered transition in the deliverable lifecycle. Each transition binds a state
// change to a target environment, the gate that governs it, the evidence that gate
// requires, and the minimum edition that unlocks it. ApprovalAction is populated only
// for the Preview -> Approved gate (the governed Console SuggestedAction); ApprovalSource
// records which trigger produced it (Console today, ticket-side later). PromotionPlan is
// populated only for the Approved -> Published gate, carrying the reused
// ReleaseOrchestrationPlanner gated-promotion output rather than a bespoke engine.
internal sealed record DeliverableTransition(
    DeliverableLifecycleState FromState,
    DeliverableLifecycleState ToState,
    string TargetEnvironment,
    string Gate,
    IReadOnlyList<string> RequiredEvidence,
    string RequiredEdition,
    string PromotionMode,
    string? ApprovalSource,
    SuggestedAction? ApprovalAction,
    ReleaseOrchestrationPlan? PromotionPlan);

// The full plan-only lifecycle for a deliverable: the ordered transitions from the
// current state to Published, the edition required for the cross-environment promotion
// step, and whether the requested lifecycle is fully unlocked by the caller's edition.
// Read-only by construction — describes the path, never executes it.
internal sealed record DeliverableLifecyclePlan(
    string DeliverableId,
    string WorkItemId,
    string Kind,
    DeliverableLifecycleState CurrentState,
    string LowerEnvironment,
    string PublishEnvironment,
    IReadOnlyList<DeliverableTransition> Transitions,
    bool CrossEnvironmentPromotionPlanned,
    bool CrossEnvironmentPromotionUnlocked,
    string CallerEdition);
