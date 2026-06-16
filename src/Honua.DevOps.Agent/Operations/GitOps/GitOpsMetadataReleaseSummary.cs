namespace Honua.DevOps.Agent.Operations.GitOps;

// Round-trip metadata GitOps PR workflow (issue #57, AC#4).
//
// Metadata-release semantics projected onto the honua-gitops plan output so Console and AI
// DevOps can see, without re-reading the (separately-owned) release package, which semantic
// Honua resources a plan carries, the server compatibility verdict, data-script coverage, and
// the rollback posture. This is a read-only projection of a MetadataReleaseChangeSet: it never
// mutates state and adds nothing that the change set did not already establish.
internal sealed record GitOpsMetadataReleaseSummary(
    string ReleasePackageId,
    string Readiness,
    IReadOnlyList<MetadataResourceSummary> SemanticResources,
    string CompatibilityStatus,
    int BreakingChanges,
    int Warnings,
    string ScriptCoverage,
    string RollbackClassification,
    string? KnownGoodRevision,
    IReadOnlyList<string> BlockingReasons);
