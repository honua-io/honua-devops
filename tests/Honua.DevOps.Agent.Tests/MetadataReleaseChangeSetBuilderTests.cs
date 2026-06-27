using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

public sealed class MetadataReleaseChangeSetBuilderTests
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

    [Fact]
    public void TryBuild_ReadyPackage_ProducesDeterministicPrReadyChangeSet()
    {
        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(ReadyPackage, out MetadataReleaseChangeSet changeSet, out string? error));
        Assert.Null(error);

        Assert.Equal("roads-api", changeSet.Service);
        Assert.Equal("release/2026.03", changeSet.DesiredRevision);
        Assert.Equal(["staging", "prod"], changeSet.TargetEnvironments);
        Assert.Equal(MetadataChangeSetReadiness.Ready, changeSet.Readiness);
        Assert.Empty(changeSet.BlockingReasons);

        Assert.StartsWith("honua-devops/metadata-release/roads-api-release-2026-03-", changeSet.BranchName, StringComparison.Ordinal);
        Assert.Contains("Metadata release roads-api release/2026.03", changeSet.CommitMessage, StringComparison.Ordinal);
        Assert.Contains("Release-Package: rel-2026-03-meta", changeSet.CommitMessage, StringComparison.Ordinal);
        Assert.Contains("Readiness: **ready**", changeSet.PrBody, StringComparison.Ordinal);

        // One platform-release manifest + one overlay per environment, plus scripts, rollback, evidence.
        Assert.Contains(changeSet.Files, file => file.Path == "desired-state/releases/roads-api/staging.platformrelease.yaml");
        Assert.Contains(changeSet.Files, file => file.Path == "desired-state/releases/roads-api/prod.platformrelease.yaml");
        Assert.Contains(changeSet.Files, file => file.Path == "desired-state/releases/roads-api/overlays/staging.overlay.yaml");
        Assert.Contains(changeSet.Files, file => file.Kind == "scripts");
        Assert.Contains(changeSet.Files, file => file.Kind == "rollback-policy");
        Assert.Contains(changeSet.Files, file => file.Kind == "evidence");

        Assert.Equal(2, changeSet.SemanticResources.Count);
    }

    [Fact]
    public void TryBuild_IsDeterministic_AcrossRuns()
    {
        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(ReadyPackage, out MetadataReleaseChangeSet first, out _));
        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(ReadyPackage, out MetadataReleaseChangeSet second, out _));

        Assert.Equal(first.BranchName, second.BranchName);
        Assert.Equal(first.CommitMessage, second.CommitMessage);
        Assert.Equal(first.PrBody, second.PrBody);
        Assert.Equal(
            first.Files.Select(file => $"{file.Path}|{file.Content}"),
            second.Files.Select(file => $"{file.Path}|{file.Content}"));
    }

    [Fact]
    public void TryBuild_BreakingCompatibility_BlocksChangeSet()
    {
        const string blocked = """
        {
          "releaseId": "rel-bad",
          "service": "roads-api",
          "desiredRevision": "release/2026.04",
          "targetEnvironments": ["prod"],
          "compatibility": { "status": "incompatible", "breakingChanges": 2, "warnings": 0 },
          "rollbackPolicy": { "classification": "manual", "knownGoodRevision": "release/2026.03" }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(blocked, out MetadataReleaseChangeSet changeSet, out _));
        Assert.Equal(MetadataChangeSetReadiness.Blocked, changeSet.Readiness);
        Assert.NotEmpty(changeSet.BlockingReasons);
        Assert.Contains(changeSet.BlockingReasons, reason => reason.Contains("breaking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryBuild_ManualRollback_EmitsApprovalGatedRollbackCommands()
    {
        const string manual = """
        {
          "releaseId": "rel-manual",
          "service": "roads-api",
          "desiredRevision": "release/2026.04",
          "targetEnvironments": ["staging"],
          "compatibility": { "status": "compatible" },
          "rollbackPolicy": { "classification": "manual", "knownGoodRevision": "release/2026.03" }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(manual, out MetadataReleaseChangeSet changeSet, out _));
        Assert.Equal(MetadataRollbackClass.Manual, changeSet.RollbackClassification);
        Assert.Equal("release/2026.03", changeSet.KnownGoodRevision);
        Assert.Contains(changeSet.RollbackCommands, command => command.StartsWith("# manual rollback", StringComparison.Ordinal));
        Assert.Contains(
            changeSet.RollbackCommands,
            command => command.Contains("honua-gitops rollback", StringComparison.Ordinal)
                && command.Contains("--to-revision release/2026.03", StringComparison.Ordinal));
    }

    [Fact]
    public void TryBuild_IrreversibleRollback_ProducesForwardFixOnlyNote()
    {
        const string irreversible = """
        {
          "releaseId": "rel-irrev",
          "service": "roads-api",
          "desiredRevision": "release/2026.05",
          "targetEnvironments": ["staging"],
          "compatibility": { "status": "compatible" },
          "rollbackPolicy": { "classification": "irreversible" }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(irreversible, out MetadataReleaseChangeSet changeSet, out _));
        Assert.Equal(MetadataRollbackClass.Irreversible, changeSet.RollbackClassification);
        Assert.Single(changeSet.RollbackCommands);
        Assert.Contains("forward-fix only", changeSet.RollbackCommands[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_KeepsSecretsOutOfGeneratedFiles()
    {
        const string withSecret = """
        {
          "releaseId": "rel-secret",
          "service": "roads-api",
          "desiredRevision": "release/2026.06",
          "targetEnvironments": ["staging"],
          "semanticResources": [
            { "kind": "Layer", "name": "roads api_key=super-secret-value token=abcdef123456", "action": "upsert" }
          ],
          "compatibility": { "status": "compatible" },
          "rollbackPolicy": { "classification": "not-required" }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(withSecret, out MetadataReleaseChangeSet changeSet, out _));
        foreach (MetadataReleaseFile file in changeSet.Files)
        {
            Assert.DoesNotContain("super-secret-value", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdef123456", file.Content, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("super-secret-value", changeSet.PrBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_NotRequiredRollback_EmitsNoRollbackCommands()
    {
        const string none = """
        {
          "releaseId": "rel-none",
          "service": "roads-api",
          "desiredRevision": "release/2026.07",
          "targetEnvironments": ["staging"],
          "compatibility": { "status": "compatible" },
          "rollbackPolicy": { "classification": "not-required" }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(none, out MetadataReleaseChangeSet changeSet, out _));
        Assert.Equal(MetadataRollbackClass.NotRequired, changeSet.RollbackClassification);
        Assert.Empty(changeSet.RollbackCommands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    public void TryBuild_InvalidDocument_FailsWithError(string payload)
    {
        Assert.False(MetadataReleaseChangeSetBuilder.TryBuild(payload, out MetadataReleaseChangeSet changeSet, out string? error));
        Assert.Null(changeSet);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // honua-server #2184 emits, on workflow publish, a native MetadataReleasePackage whose
    // `entries[]` carry {semanticId, artifactKind: "workflow", changeClass: "content"} rather
    // than the generic semanticResources[]{kind,name,action} projection. This proves a published
    // workflow release entry flows end-to-end into a changeset entry (resource + manifest content)
    // with the right additive (upsert) classification, so it can't silently regress to "not
    // rejected but never consumed".
    private const string WorkflowReleasePackage = """
    {
      "packageId": "11111111-2222-3333-4444-555555555555",
      "metadata": { "name": "workflow-roads-clip", "namespace": "geo" },
      "sourceEnvironment": "authoring",
      "sourceRevision": 7,
      "sourceEtag": "sha256:abc123",
      "targetEnvironments": ["staging", "prod"],
      "status": "ready",
      "entries": [
        {
          "semanticId": "workflow:roads-clip",
          "artifactKind": "workflow",
          "desiredMetadataRevision": 7,
          "desiredContentVersionId": "sha256:deadbeef",
          "changeClass": "content",
          "status": "ready",
          "targetStates": [
            { "environment": "staging", "bindingState": "missing" },
            { "environment": "prod", "bindingState": "missing" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void TryBuild_WorkflowReleaseEntry_FlowsIntoChangeSet()
    {
        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(WorkflowReleasePackage, out MetadataReleaseChangeSet changeSet, out string? error));
        Assert.Null(error);

        // The native entries[] workflow entry surfaced as a semantic resource on the change set.
        MetadataResourceSummary workflow = Assert.Single(changeSet.SemanticResources);
        Assert.Equal("workflow", workflow.Kind);
        Assert.Equal("workflow:roads-clip", workflow.Name);
        // changeClass "content" is additive -> upsert (never a destructive delete/binding).
        Assert.Equal("upsert", workflow.Action);

        // And it lands in the per-environment PlatformRelease manifest content as a semantic
        // resource, i.e. it actually becomes a changeset entry rather than just "not rejected".
        MetadataReleaseFile stagingManifest = Assert.Single(
            changeSet.Files,
            file => file.Path == "desired-state/releases/workflow-roads-clip/staging.platformrelease.yaml");
        Assert.Contains("kind: 'workflow'", stagingManifest.Content, StringComparison.Ordinal);
        Assert.Contains("name: 'workflow:roads-clip'", stagingManifest.Content, StringComparison.Ordinal);
        Assert.Contains("action: 'upsert'", stagingManifest.Content, StringComparison.Ordinal);

        // Identity is derived from the server package metadata.name, and the workflow shows up in
        // the PR body so an operator/AI can see the promoted artifact.
        Assert.Equal("workflow-roads-clip", changeSet.Service);
        Assert.Contains("`workflow/workflow:roads-clip` (upsert)", changeSet.PrBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_WorkflowReleaseEntry_UnderSpecWrapper_FlowsIntoChangeSet()
    {
        // The GitOps-safe manifest nests entries under spec.entries; ensure that shape is read too.
        const string specWrapped = """
        {
          "metadata": { "name": "workflow-parcels-buffer" },
          "sourceEnvironment": "authoring",
          "sourceRevision": 3,
          "targetEnvironments": ["staging"],
          "spec": {
            "entries": [
              {
                "semanticId": "workflow:parcels-buffer",
                "artifactKind": "workflow",
                "changeClass": "content"
              }
            ]
          }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(specWrapped, out MetadataReleaseChangeSet changeSet, out _));
        MetadataResourceSummary workflow = Assert.Single(changeSet.SemanticResources);
        Assert.Equal("workflow", workflow.Kind);
        Assert.Equal("workflow:parcels-buffer", workflow.Name);
        Assert.Equal("upsert", workflow.Action);
    }

    [Fact]
    public void TryBuild_MissingEnvironments_DefaultsToStaging()
    {
        const string noEnvironments = """
        {
          "releaseId": "rel-default-env",
          "service": "roads-api",
          "desiredRevision": "release/2026.08",
          "compatibility": { "status": "compatible" },
          "rollbackPolicy": { "classification": "not-required" }
        }
        """;

        Assert.True(MetadataReleaseChangeSetBuilder.TryBuild(noEnvironments, out MetadataReleaseChangeSet changeSet, out _));
        Assert.Equal(["staging"], changeSet.TargetEnvironments);
    }
}
