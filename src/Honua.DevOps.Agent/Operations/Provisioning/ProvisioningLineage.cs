namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Machine-readable pre-server lineage. These are references to evidence authorities, never aliases
/// for later server operation/proposal identifiers. Missing downstream identities remain absent.
/// </summary>
internal sealed record ProvisioningLineage(
    string ProvisioningOperationId,
    string? PlanSha256 = null,
    string? ApprovalReceiptId = null,
    string? ApprovalReceiptSha256 = null,
    string? ApplyAuditEventId = null,
    string? ActuatorReceiptReference = null,
    string? HandoffReceiptSha256 = null,
    string? HandoffVerificationReceiptId = null,
    string? HandoffVerificationReceiptSha256 = null,
    string? RootProvisioningOperationId = null,
    string? ServerOperationId = null,
    string? ServerProposalId = null,
    string? ServerDecisionReference = null,
    string? ServerExecutionId = null,
    string? ReleaseReceiptReference = null,
    string? ReleaseReceiptSha256 = null);
