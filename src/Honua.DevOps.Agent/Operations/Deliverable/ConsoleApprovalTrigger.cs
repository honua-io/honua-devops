using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Operations.Deliverable;

// Console approval surface = the approval system-of-record for the Preview -> Approved
// gate (founder decision). Emits the steward sign-off as a governed Console
// SuggestedAction: RequiresApproval=true so Console routes it through the approval
// path, MutatesState=true because approval advances the lifecycle, and a stable
// TargetOperationId so the action threads back to the deliverable's operation.
//
// The trigger never approves anything itself; it only describes the action the
// approval surface must clear. A future TicketApprovalTrigger implements the same
// interface to let a Jira status transition drive the identical state machine.
internal sealed class ConsoleApprovalTrigger : IDeliverableApprovalTrigger
{
    internal const string SourceId = "console-approval";

    public string Source => SourceId;

    public SuggestedAction BuildApprovalAction(Deliverable deliverable, string targetOperationId)
    {
        ArgumentNullException.ThrowIfNull(deliverable);

        return new SuggestedAction(
            Id: $"approve-deliverable:{deliverable.DeliverableId}",
            Title: "Approve deliverable for publish",
            Description:
                $"Steward sign-off to advance deliverable `{deliverable.DeliverableId}` " +
                $"({deliverable.Kind}) from preview in `{deliverable.Environment}` to approved. " +
                "Routed through the Console approval surface; honua-devops never approves on its own.",
            RequiresApproval: true,
            MutatesState: true,
            TargetOperationId: targetOperationId,
            WorkflowLink: null,
            Kind: "deliverable-approval");
    }
}
