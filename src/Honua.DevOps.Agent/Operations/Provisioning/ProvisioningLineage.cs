using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Machine-readable pre-server lineage. These are references to evidence authorities, never aliases
/// for later server operation/proposal identifiers. Missing downstream identities remain absent.
/// </summary>
internal sealed record ProvisioningLineage(
    [property: JsonPropertyName("provisioningOperationId")] string ProvisioningOperationId,
    [property: JsonPropertyName("planSha256")] string? PlanSha256 = null,
    /// <summary>
    /// The honua-iac exact-plan metadata digest an approval binds to. This — not the
    /// raw <c>.tfplan</c> hash — is what <c>terraform-exact-apply.sh</c> checks
    /// against <c>--approved-digest</c>, and it transitively covers the plan bytes,
    /// the backend, the account/role, the inputs and the prior state.
    /// </summary>
    [property: JsonPropertyName("planMetadataDigest")] string? PlanMetadataDigest = null,
    [property: JsonPropertyName("approvalReceiptId")] string? ApprovalReceiptId = null,
    [property: JsonPropertyName("approvalReceiptSha256")] string? ApprovalReceiptSha256 = null,
    [property: JsonPropertyName("applyAuditEventId")] string? ApplyAuditEventId = null,
    [property: JsonPropertyName("actuatorReceiptReference")] string? ActuatorReceiptReference = null,
    [property: JsonPropertyName("handoffReceiptSha256")] string? HandoffReceiptSha256 = null,
    [property: JsonPropertyName("handoffVerificationReceiptId")] string? HandoffVerificationReceiptId = null,
    [property: JsonPropertyName("handoffVerificationReceiptSha256")] string? HandoffVerificationReceiptSha256 = null,
    [property: JsonPropertyName("rootProvisioningOperationId")] string? RootProvisioningOperationId = null,
    [property: JsonPropertyName("serverOperationId")] string? ServerOperationId = null,
    [property: JsonPropertyName("serverProposalId")] string? ServerProposalId = null,
    [property: JsonPropertyName("serverDecisionReference")] string? ServerDecisionReference = null,
    [property: JsonPropertyName("serverExecutionId")] string? ServerExecutionId = null,
    [property: JsonPropertyName("releaseReceiptReference")] string? ReleaseReceiptReference = null,
    [property: JsonPropertyName("releaseReceiptSha256")] string? ReleaseReceiptSha256 = null);
