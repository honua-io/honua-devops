namespace Honua.DevOps.Agent.Operations.GitOps;

// Round-trip metadata GitOps PR workflow (issue #57, first slice).
//
// These records model the PR-ready desired-state change set produced from a validated
// metadata release package supplied by honua-server (#1163/#1164/#1165). The builder is
// read-only: it interprets the supplied package and emits deterministic Git artifacts
// (branch name, commit message, PR body, file path -> content map) plus rollback commands.
// It never writes Git, opens a PR, or creates a server operation; Console / the governed
// proposal path owns the actual mutation.

// Rollback classification carried on a metadata release change set so Console can render the
// recovery posture and the builder can emit the matching rollback commands without parsing
// prose. Mirrors the bridge ConsoleBridge.RollbackClass vocabulary but lives in GitOps so the
// change-set surface does not depend on the (separately-owned) Console explanation contracts.
internal static class MetadataRollbackClass
{
    internal const string Automatic = "automatic";
    internal const string Manual = "manual";
    internal const string Irreversible = "irreversible";
    internal const string NotRequired = "not-required";
    internal const string Unknown = "unknown";
}

// Overall readiness of the change set derived from the supplied compatibility report and
// script-coverage evidence. "blocked" means the change set should not be turned into a PR
// as-is (breaking compatibility or an uncovered required script); "ready"/"warning" can
// proceed through the governed proposal path.
internal static class MetadataChangeSetReadiness
{
    internal const string Ready = "ready";
    internal const string Warning = "warning";
    internal const string Blocked = "blocked";
    internal const string Unknown = "unknown";
}

// One generated desired-state (or scripts/rollback) file in the change set. Path is repo-
// relative and deterministic; Content is the already-rendered, secret-free file body. Kind
// classifies the file so Console can group the PR contents (manifest, overlay, scripts,
// rollback-policy, evidence).
internal sealed record MetadataReleaseFile(
    string Path,
    string Kind,
    string Content);

// A semantic Honua resource carried by the release package (layer, style, dataset binding,
// ...). Summarised onto the change set and the gitops plan so the metadata release is visible
// without re-reading the package.
internal sealed record MetadataResourceSummary(
    string Kind,
    string Name,
    string Action,
    string? Detail);

// A reference back to a piece of release-package evidence (compatibility report, script-
// coverage report, server release record). RawRef is the durable pointer; the builder keeps
// these as opaque references and never inlines secret-bearing payloads into Git files.
internal sealed record MetadataEvidenceLink(
    string Type,
    string Reference,
    string Summary);

// One structured section of a read-only explanation of the proposed GitOps PR operation
// (issue #57 AC#3). Section names the facet ("pr-operation", "compatibility", "script-coverage",
// "rollback", "files"); Status reuses MetadataChangeSetReadiness so a caller can colour the
// section without parsing Detail. Findings are short, already-secret-free human-readable bullets.
internal sealed record MetadataChangeSetExplanationSection(
    string Section,
    string Status,
    string Detail,
    IReadOnlyList<string> Findings);

// A read-only, evidence-linked explanation of the proposed metadata-release GitOps PR operation.
// AI DevOps hands a validated release package to the builder and gets back this projection so it
// can summarize and explain WHAT the proposed PR will do — which branch it targets, what files it
// will contain (grouped by kind), the semantic resources, the server compatibility verdict, data
// script coverage, and the rollback posture — without writing Git, opening a PR, applying
// manifests, or creating a server operation. Summary is the single human-readable paragraph a
// caller can surface verbatim; everything else is structured. Deterministic for a given package.
internal sealed record MetadataChangeSetExplanation(
    string ReleasePackageId,
    string Service,
    string DesiredRevision,
    IReadOnlyList<string> TargetEnvironments,
    string Readiness,
    string BranchName,
    string Summary,
    IReadOnlyList<MetadataChangeSetExplanationSection> Sections,
    IReadOnlyList<MetadataResourceSummary> SemanticResources,
    IReadOnlyList<MetadataEvidenceLink> EvidenceLinks,
    string RollbackClassification,
    string? KnownGoodRevision,
    IReadOnlyList<string> RollbackCommands,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings);

// The PR-ready change set. Everything here is deterministic for a given release package so a
// re-run produces an identical branch, commit, body, and files (idempotent PR updates).
internal sealed record MetadataReleaseChangeSet(
    string ReleasePackageId,
    string Service,
    string DesiredRevision,
    IReadOnlyList<string> TargetEnvironments,
    string Readiness,
    string BranchName,
    string CommitMessage,
    string PrTitle,
    string PrBody,
    IReadOnlyList<MetadataResourceSummary> SemanticResources,
    IReadOnlyList<MetadataReleaseFile> Files,
    IReadOnlyList<MetadataEvidenceLink> EvidenceLinks,
    string RollbackClassification,
    string? KnownGoodRevision,
    IReadOnlyList<string> RollbackCommands,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings);
