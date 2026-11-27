using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

internal sealed class PatchScorecardUpdate
{
    [JsonPropertyName("regressionTestAdded")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RegressionTestAdded { get; init; }

    [JsonPropertyName("failedBeforePatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FailedBeforePatch { get; init; }

    [JsonPropertyName("passedAfterPatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PassedAfterPatch { get; init; }

    [JsonPropertyName("ciPassed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CiPassed { get; init; }

    [JsonPropertyName("interopMatrixPassed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InteropMatrixPassed { get; init; }

    [JsonPropertyName("blastRadius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlastRadius { get; init; }

    [JsonPropertyName("filesTouchedCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FilesTouchedCount { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("rollbackNotes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RollbackNotes { get; init; }
}

internal sealed class PatchStatusUpdate
{
    internal static readonly HashSet<string> AllowedStages = new(StringComparer.Ordinal)
    {
        "queued",
        "reproducing",
        "test-failed-verified",
        "patch-pr-opened",
        "ci-passed",
        "awaiting-approval",
        "approved",
        "build-cut",
        "released",
        "rejected",
        "abandoned"
    };

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("component")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Component { get; init; }

    [JsonPropertyName("agentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentRunId { get; init; }

    [JsonPropertyName("branchRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BranchRef { get; init; }

    [JsonPropertyName("prNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PrNumber { get; init; }

    [JsonPropertyName("publicPrUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicPrUrl { get; init; }

    [JsonPropertyName("isPublicRepo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPublicRepo { get; init; }

    [JsonPropertyName("approvedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovedBy { get; init; }

    [JsonPropertyName("buildChannel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildChannel { get; init; }

    [JsonPropertyName("buildArtifactRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildArtifactRef { get; init; }

    [JsonPropertyName("buildArtifactDigest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildArtifactDigest { get; init; }

    [JsonPropertyName("releaseVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseVersion { get; init; }

    [JsonPropertyName("releaseNotesUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseNotesUrl { get; init; }

    [JsonPropertyName("fixedInServerVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixedInServerVersion { get; init; }

    [JsonPropertyName("fixedInSdkVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixedInSdkVersion { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("propagateToFingerprintMatches")]
    public bool PropagateToFingerprintMatches { get; init; } = true;

    [JsonPropertyName("scorecard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PatchScorecardUpdate? Scorecard { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Stage) || !AllowedStages.Contains(Stage.Trim()))
        {
            throw new ArgumentException($"Unsupported patch lifecycle stage '{Stage}'.", nameof(Stage));
        }

        if (Stage is "build-cut" or "released" &&
            (Scorecard is null ||
             Scorecard.RegressionTestAdded != true ||
             Scorecard.FailedBeforePatch != true ||
             Scorecard.PassedAfterPatch != true ||
             Scorecard.CiPassed != true))
        {
            throw new InvalidOperationException(
                $"Stage '{Stage}' requires a passing Test-First Patch Contract scorecard (regressionTestAdded, failedBeforePatch, passedAfterPatch, ciPassed).");
        }
    }
}

internal sealed class AwsObservabilityCollectRequest
{
    [JsonPropertyName("scopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Scopes { get; init; }

    [JsonPropertyName("cloudWatchNamespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloudWatchNamespace { get; init; }

    [JsonPropertyName("ecsCluster")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EcsCluster { get; init; }

    [JsonPropertyName("eksClusterName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EksClusterName { get; init; }
}

internal sealed class OpsLoopFixProposalRequest
{
    [JsonPropertyName("findingId")]
    public required string FindingId { get; init; }

    [JsonPropertyName("actionId")]
    public required string ActionId { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("deliveryMode")]
    public string DeliveryMode { get; init; } = "direct";

    [JsonPropertyName("awsEvidenceIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AwsEvidenceIds { get; init; }
}

internal sealed class TicketMessageActionPrompt
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }
}

internal sealed class SupportTicketMessageRequest
{
    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("authorRole")]
    public string AuthorRole { get; init; } = "agent";

    [JsonPropertyName("visibility")]
    public string Visibility { get; init; } = "public";

    [JsonPropertyName("actionPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TicketMessageActionPrompt? ActionPrompt { get; init; }
}

internal sealed class SecurityAdvisoryPublishRequest
{
    [JsonPropertyName("advisoryId")]
    public required string AdvisoryId { get; init; }

    [JsonPropertyName("cveId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CveId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("component")]
    public required string Component { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("vexStatus")]
    public string VexStatus { get; init; } = "affected";

    [JsonPropertyName("cisaKev")]
    public bool CisaKev { get; init; }

    [JsonPropertyName("affectedVersionRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AffectedVersionRange { get; init; }

    [JsonPropertyName("affectedDigests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AffectedDigests { get; init; }

    [JsonPropertyName("fixedVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixedVersion { get; init; }

    [JsonPropertyName("fixedDigest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixedDigest { get; init; }

    [JsonPropertyName("containmentGuidance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainmentGuidance { get; init; }

    [JsonPropertyName("embargoed")]
    public bool Embargoed { get; init; }

    [JsonPropertyName("autoCreateTickets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoCreateTickets { get; init; }
}
