using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Operations.Deliverable;

// A deliverable is the artifact a work item (from the #83 intake connector) asks for
// — a map, analysis, dashboard, or app — tracked through the four-state lifecycle and
// bound to an environment. This PR is plan-only: a Deliverable describes intent and
// provenance; honua-devops never generates the artifact or mutates its state here.
//
// PreviewUrl is null until a lower-environment preview exists (never fabricated, like
// WorkflowLink.Available=false). Provenance reuses the Console EvidenceRef shape so the
// same references can be surfaced to the Console approval surface or written back to a
// ticket without minting a parallel evidence vocabulary.
internal sealed record Deliverable(
    string DeliverableId,
    string WorkItemId,
    string Kind,
    DeliverableLifecycleState State,
    string Environment,
    string? PreviewUrl,
    IReadOnlyList<EvidenceRef> Provenance);
