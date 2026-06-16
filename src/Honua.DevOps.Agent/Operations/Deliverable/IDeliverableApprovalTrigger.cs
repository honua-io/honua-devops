using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Operations.Deliverable;

// The system-of-record seam for the Preview -> Approved gate. Founder decision: the
// approval system-of-record is the Console approval surface, so the only trigger
// implemented now is ConsoleApprovalTrigger, which emits the gate as a governed
// SuggestedAction (RequiresApproval=true). The abstraction exists so a later PR can
// add a ticket-side trigger (e.g. a Jira status transition driving the same state
// machine) without touching the planner — the planner asks the trigger to describe
// the approval action and stays source-agnostic.
internal interface IDeliverableApprovalTrigger
{
    // Stable identifier for where the approval originates (e.g. "console-approval",
    // and later "jira-status-transition"). Recorded on the transition so callers can
    // route the gate to the correct surface.
    string Source { get; }

    // Build the governed approval action for promoting a deliverable from Preview to
    // Approved. The action is advisory by construction — RequiresApproval=true and the
    // bridge never invokes it; it hands the deliverable to the approval surface.
    SuggestedAction BuildApprovalAction(Deliverable deliverable, string targetOperationId);
}
