namespace Honua.DevOps.Agent.Operations.Deliverable;

// The four-state deliverable lifecycle from issue #77, bound to environments by the
// planner: Draft (work item accepted, nothing built), Preview (rendered in a lower
// environment behind a preview link), Approved (steward signed off via the Console
// approval surface), Published (promoted to prod through the gated-promotion engine).
// Transitions only ever advance one step; this enum carries no execution semantics.
internal enum DeliverableLifecycleState
{
    Draft,
    Preview,
    Approved,
    Published
}
