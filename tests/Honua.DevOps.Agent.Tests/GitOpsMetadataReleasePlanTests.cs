using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// Issue #57, AC#4: honua-gitops plan output carries metadata-release semantics (semantic resource
// summaries, compatibility verdict, script coverage, rollback classification) and a per-environment
// metadata target status. These tests project a read-only MetadataReleaseChangeSet onto a plan and
// assert the summary + per-environment targeting, mirroring the fixtures used by
// MetadataReleaseChangeSetBuilderTests.
public sealed class GitOpsMetadataReleasePlanTests
{
    private const string ReadyPackage = """
    {
      "releaseId": "rel-2026-03-meta",
      "service": "roads-api",
      "desiredRevision": "release/2026.03",
      "targetEnvironments": ["staging", "prod"],
      "semanticResources": [
        { "kind": "Layer", "name": "roads", "action": "upsert" },
        { "kind": "Style", "name": "roads-default", "action": "upsert" }
      ],
      "compatibility": { "status": "compatible", "breakingChanges": 0, "warnings": 0 },
      "scriptCoverage": { "covered": 2, "total": 2, "uncovered": [] },
      "rollbackPolicy": { "classification": "automatic", "knownGoodRevision": "release/2026.02" }
    }
    """;

    private const string BlockedPackage = """
    {
      "releaseId": "rel-bad",
      "service": "roads-api",
      "desiredRevision": "release/2026.04",
      "targetEnvironments": ["prod"],
      "compatibility": { "status": "incompatible", "breakingChanges": 2, "warnings": 0 },
      "rollbackPolicy": { "classification": "manual", "knownGoodRevision": "release/2026.03" }
    }
    """;

    private const string PartialCoveragePackage = """
    {
      "releaseId": "rel-coverage",
      "service": "roads-api",
      "desiredRevision": "release/2026.09",
      "targetEnvironments": ["staging"],
      "compatibility": { "status": "compatible", "breakingChanges": 0, "warnings": 0 },
      "scriptCoverage": { "covered": 1, "total": 3, "uncovered": ["migrate-roads", "seed-styles"] },
      "rollbackPolicy": { "classification": "manual", "knownGoodRevision": "release/2026.08" }
    }
    """;

    [Fact]
    public void SummarizeMetadataRelease_ReadyPackage_ProjectsSemanticsAndCompatibility()
    {
        MetadataReleaseChangeSet changeSet = Build(ReadyPackage);

        GitOpsMetadataReleaseSummary summary = GitOpsPlanner.SummarizeMetadataRelease(changeSet);

        Assert.Equal("rel-2026-03-meta", summary.ReleasePackageId);
        Assert.Equal(MetadataChangeSetReadiness.Ready, summary.Readiness);
        Assert.Equal("compatible", summary.CompatibilityStatus);
        Assert.Equal(0, summary.BreakingChanges);
        Assert.Equal(0, summary.Warnings);
        Assert.Equal("covered", summary.ScriptCoverage);
        Assert.Equal(MetadataRollbackClass.Automatic, summary.RollbackClassification);
        Assert.Equal("release/2026.02", summary.KnownGoodRevision);
        Assert.Empty(summary.BlockingReasons);

        Assert.Equal(2, summary.SemanticResources.Count);
        Assert.Contains(summary.SemanticResources, resource => resource.Kind == "Layer" && resource.Name == "roads");
        Assert.Contains(summary.SemanticResources, resource => resource.Kind == "Style" && resource.Name == "roads-default");
    }

    [Fact]
    public void SummarizeMetadataRelease_PartialScriptCoverage_ReportsRatio()
    {
        MetadataReleaseChangeSet changeSet = Build(PartialCoveragePackage);

        GitOpsMetadataReleaseSummary summary = GitOpsPlanner.SummarizeMetadataRelease(changeSet);

        Assert.Equal(MetadataChangeSetReadiness.Warning, summary.Readiness);
        Assert.Equal("compatible-with-warnings", summary.CompatibilityStatus);
        Assert.Equal("1/3", summary.ScriptCoverage);
        Assert.True(summary.Warnings >= 1);
    }

    [Fact]
    public void SummarizeMetadataRelease_BlockedPackage_SurfacesBlockingReasonsAndBreakingCount()
    {
        MetadataReleaseChangeSet changeSet = Build(BlockedPackage);

        GitOpsMetadataReleaseSummary summary = GitOpsPlanner.SummarizeMetadataRelease(changeSet);

        Assert.Equal(MetadataChangeSetReadiness.Blocked, summary.Readiness);
        Assert.Equal("incompatible", summary.CompatibilityStatus);
        Assert.Equal(2, summary.BreakingChanges);
        Assert.NotEmpty(summary.BlockingReasons);
        Assert.Equal(MetadataRollbackClass.Manual, summary.RollbackClassification);
    }

    [Fact]
    public void AttachMetadataRelease_TagsTargetedVsNotTargetedEnvironments()
    {
        MetadataReleaseChangeSet changeSet = Build(BlockedPackage); // targets prod only
        GitOpsPlan plan = StubPlan("staging", "prod");

        GitOpsPlan projected = GitOpsPlanner.AttachMetadataRelease(plan, changeSet);

        GitOpsEnvironmentPlan staging = projected.Environments.Single(environment => environment.Environment == "staging");
        GitOpsEnvironmentPlan prod = projected.Environments.Single(environment => environment.Environment == "prod");

        Assert.Equal(GitOpsPlanner.MetadataTargetStatus.NotTargeted, staging.MetadataTargetStatus);
        Assert.Equal(GitOpsPlanner.MetadataTargetStatus.InScope, prod.MetadataTargetStatus);

        Assert.NotNull(projected.MetadataRelease);
        Assert.Equal("rel-bad", projected.MetadataRelease!.ReleasePackageId);
    }

    [Fact]
    public void AttachMetadataRelease_DoesNotMutateInputsAndIsDeterministic()
    {
        MetadataReleaseChangeSet changeSet = Build(ReadyPackage);
        GitOpsPlan plan = StubPlan("staging", "prod");

        GitOpsPlan first = GitOpsPlanner.AttachMetadataRelease(plan, changeSet);
        GitOpsPlan second = GitOpsPlanner.AttachMetadataRelease(plan, changeSet);

        // The source plan is unchanged (records are projected onto fresh instances, not mutated).
        Assert.Null(plan.MetadataRelease);
        Assert.All(plan.Environments, environment => Assert.Null(environment.MetadataTargetStatus));

        // Two projections of the same inputs are value-equal.
        Assert.Equal(
            first.Environments.Select(environment => $"{environment.Environment}|{environment.MetadataTargetStatus}"),
            second.Environments.Select(environment => $"{environment.Environment}|{environment.MetadataTargetStatus}"));
        Assert.Equal(first.MetadataRelease, second.MetadataRelease);
    }

    private static MetadataReleaseChangeSet Build(string package)
    {
        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(package, out MetadataReleaseChangeSet changeSet, out _));
        return changeSet;
    }

    private static GitOpsPlan StubPlan(params string[] environments)
    {
        GitOpsEnvironmentPlan[] environmentPlans = environments
            .Select(environment => new GitOpsEnvironmentPlan(
                Environment: environment,
                ActualRevision: "unknown",
                DesiredRevision: "release/2026.03",
                DiffStatus: "actual-state-pending",
                GateStatus: "plan-only",
                Drift: [],
                Commands: []))
            .ToArray();

        return new GitOpsPlan(
            Engine: "honua-gitops",
            RequestedAction: "plan",
            EffectiveAction: "plan",
            ActualStateSource: "manifest-export-unavailable",
            DiffSummary: "desired=release/2026.03",
            DriftSummary: "infra-checks=0; release-checks=0; service-state-checks=0",
            GateStatus: "plan-only",
            SupportedOperations: ["plan"],
            RequiredEvidence: ["manifest-diff"],
            Environments: environmentPlans,
            StateTransitions: []);
    }
}
