using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Operations.Deliverable;

// Console/ticket-facing projection of a single lifecycle transition. Reuses the Console
// bridge vocabulary (no parallel DTOs): RequiredEvidence is rendered as-is, the
// approval gate carries the governed SuggestedAction, and EditionGated marks a step the
// caller's edition does not unlock so Console can show it pending rather than enabled.
internal sealed record DeliverableTransitionProjection(
    string FromState,
    string ToState,
    string TargetEnvironment,
    string Gate,
    IReadOnlyList<string> RequiredEvidence,
    string RequiredEdition,
    bool EditionGated,
    string? ApprovalSource,
    SuggestedAction? ApprovalAction);

// Console/ticket-facing projection of the whole deliverable lifecycle plan. Read-only
// by construction: it projects an already-computed plan and never generates an artifact
// or executes a transition. PreviewLink is WorkflowLink.Available=false when no preview
// environment URL exists yet (never fabricated). Provenance reuses EvidenceRef so the
// same references can be written back to the source ticket.
internal sealed record DeliverableProjection(
    string DeliverableId,
    string WorkItemId,
    string Kind,
    string CurrentState,
    string LowerEnvironment,
    string PublishEnvironment,
    WorkflowLink PreviewLink,
    IReadOnlyList<DeliverableTransitionProjection> Transitions,
    IReadOnlyList<SuggestedAction> SuggestedActions,
    IReadOnlyList<EvidenceRef> Provenance,
    bool CrossEnvironmentPromotionPlanned,
    bool CrossEnvironmentPromotionUnlocked,
    string CallerEdition)
{
    internal static DeliverableProjection From(DeliverableLifecyclePlan plan, Deliverable deliverable)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(deliverable);

        bool enterpriseUnlocked = plan.CrossEnvironmentPromotionUnlocked;

        List<DeliverableTransitionProjection> transitions = [];
        List<SuggestedAction> suggestedActions = [];
        foreach (DeliverableTransition transition in plan.Transitions)
        {
            // A transition is edition-gated when it requires Enterprise but the caller
            // is below it (the cross-env promotion step); single-env Pro steps are not
            // gated for a Pro+ caller.
            bool editionGated = string.Equals(transition.RequiredEdition, DeliverableLifecyclePlanner.EnterpriseEdition, StringComparison.OrdinalIgnoreCase)
                && !enterpriseUnlocked;

            transitions.Add(new DeliverableTransitionProjection(
                FromState: transition.FromState.ToConfigValue(),
                ToState: transition.ToState.ToConfigValue(),
                TargetEnvironment: transition.TargetEnvironment,
                Gate: transition.Gate,
                RequiredEvidence: transition.RequiredEvidence,
                RequiredEdition: transition.RequiredEdition,
                EditionGated: editionGated,
                ApprovalSource: transition.ApprovalSource,
                ApprovalAction: transition.ApprovalAction));

            if (transition.ApprovalAction is not null)
            {
                suggestedActions.Add(transition.ApprovalAction);
            }
        }

        // Preview link: never fabricated. Available only when the deliverable already
        // carries a preview URL from a lower-environment render.
        bool previewAvailable = !string.IsNullOrWhiteSpace(deliverable.PreviewUrl);
        WorkflowLink previewLink = new(
            Rel: "deliverable-preview",
            Label: "Open deliverable preview",
            Href: previewAvailable ? deliverable.PreviewUrl : null,
            Available: previewAvailable);

        return new DeliverableProjection(
            DeliverableId: plan.DeliverableId,
            WorkItemId: plan.WorkItemId,
            Kind: plan.Kind,
            CurrentState: plan.CurrentState.ToConfigValue(),
            LowerEnvironment: plan.LowerEnvironment,
            PublishEnvironment: plan.PublishEnvironment,
            PreviewLink: previewLink,
            Transitions: transitions,
            SuggestedActions: suggestedActions,
            Provenance: deliverable.Provenance,
            CrossEnvironmentPromotionPlanned: plan.CrossEnvironmentPromotionPlanned,
            CrossEnvironmentPromotionUnlocked: plan.CrossEnvironmentPromotionUnlocked,
            CallerEdition: plan.CallerEdition);
    }
}
