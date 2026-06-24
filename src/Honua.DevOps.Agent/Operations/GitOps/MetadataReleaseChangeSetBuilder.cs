using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Round-trip metadata GitOps PR workflow (issue #57, first slice).
//
// Turns a validated metadata release package (server compatibility report, semantic
// resources, environment bindings, optional data scripts, rollback policy) into a
// deterministic, secret-free, PR-ready desired-state change set: a branch name, commit
// message, PR title/body with evidence links, a repo-relative file path -> content map, and
// rollback commands derived from the rollback classification and known-good revision.
//
// Boundaries:
//   * Read-only. It interprets the supplied package and renders Git artifacts in-process; it
//     never writes Git, opens/updates a PR, applies manifests, or creates a server operation.
//   * It does NOT compute compatibility or script coverage; it interprets the server-supplied
//     report (server compatibility engine is honua-server #1163/#1164/#1165).
//   * Secrets are kept out of generated files: only allow-listed scalar fields are rendered
//     into manifests, every rendered value is redaction-scrubbed, and raw package payloads are
//     carried as opaque evidence references, never inlined.
internal static class MetadataReleaseChangeSetBuilder
{
    internal const string DefaultGitOpsTool = "honua-gitops";

    internal static bool TryBuild(
        string? releasePackageJson,
        out MetadataReleaseChangeSet changeSet,
        out string? error)
    {
        changeSet = null!;
        error = null;

        if (!TryParse(releasePackageJson, out JsonElement root, out error))
        {
            return false;
        }

        string releaseId = DeploymentInputs.SanitizeFreeText(
            TryGetString(root, "releaseId", "release_id", "id"), "unknown-release");
        string service = SanitizeService(TryGetString(root, "service", "serviceName"));
        string desiredRevision = ValidateRevisionOrFallback(
            TryGetString(root, "desiredRevision", "desired_revision", "revision"), "HEAD");
        string[] environments = ReadEnvironments(root);
        string gitOpsTool = NormalizeToolToken(TryGetString(root, "gitOpsTool", "engine"), DefaultGitOpsTool);

        IReadOnlyList<MetadataResourceSummary> resources = ReadSemanticResources(root);

        (string compatibilityStatus, int breakingChanges, int compatWarnings, List<string> compatReasons) =
            ReadCompatibility(root);
        (int coveredScripts, int totalScripts, List<string> uncoveredScripts) = ReadScriptCoverage(root);
        (string rollbackClass, string? knownGoodRevision) = ReadRollback(root);

        List<string> blockingReasons = [];
        List<string> warnings = [];

        if (compatibilityStatus == MetadataChangeSetReadiness.Blocked)
        {
            blockingReasons.Add(breakingChanges > 0
                ? $"Compatibility report flags {breakingChanges} breaking change(s); release cannot be turned into a PR as-is."
                : "Compatibility report status is incompatible.");
            blockingReasons.AddRange(compatReasons);
        }
        else if (compatWarnings > 0)
        {
            warnings.Add($"Compatibility report carries {compatWarnings} advisory warning(s).");
        }

        if (totalScripts > 0 && (coveredScripts < totalScripts || uncoveredScripts.Count > 0))
        {
            int gaps = Math.Max(totalScripts - coveredScripts, uncoveredScripts.Count);
            warnings.Add($"{coveredScripts}/{totalScripts} data script(s) covered; {gaps} gap(s) remain.");
        }

        string readiness = blockingReasons.Count > 0
            ? MetadataChangeSetReadiness.Blocked
            : compatibilityStatus == MetadataChangeSetReadiness.Unknown && resources.Count == 0
                ? MetadataChangeSetReadiness.Unknown
                : warnings.Count > 0
                    ? MetadataChangeSetReadiness.Warning
                    : MetadataChangeSetReadiness.Ready;

        List<MetadataEvidenceLink> evidenceLinks = BuildEvidenceLinks(root, releaseId);

        string revisionToken = NormalizeResourceToken(desiredRevision, "head");
        string serviceToken = NormalizeResourceToken(service, "honua-service");
        string branchName = BuildBranchName(serviceToken, revisionToken, releaseId);

        List<MetadataReleaseFile> files = BuildFiles(
            service,
            serviceToken,
            desiredRevision,
            revisionToken,
            environments,
            gitOpsTool,
            resources,
            compatibilityStatus,
            breakingChanges,
            compatWarnings,
            coveredScripts,
            totalScripts,
            uncoveredScripts,
            rollbackClass,
            knownGoodRevision,
            evidenceLinks);

        List<string> rollbackCommands = BuildRollbackCommands(
            gitOpsTool, service, environments, rollbackClass, knownGoodRevision);

        string commitMessage = BuildCommitMessage(service, desiredRevision, environments, releaseId);
        string prTitle = $"metadata-release: {service} {desiredRevision} -> {string.Join(", ", environments)}";
        string prBody = BuildPrBody(
            service,
            desiredRevision,
            environments,
            releaseId,
            readiness,
            resources,
            compatibilityStatus,
            breakingChanges,
            compatWarnings,
            coveredScripts,
            totalScripts,
            uncoveredScripts,
            rollbackClass,
            knownGoodRevision,
            rollbackCommands,
            evidenceLinks,
            blockingReasons,
            warnings);

        changeSet = new MetadataReleaseChangeSet(
            ReleasePackageId: releaseId,
            Service: service,
            DesiredRevision: desiredRevision,
            TargetEnvironments: environments,
            Readiness: readiness,
            BranchName: branchName,
            CommitMessage: commitMessage,
            PrTitle: prTitle,
            PrBody: prBody,
            SemanticResources: resources,
            Files: files,
            EvidenceLinks: evidenceLinks,
            RollbackClassification: rollbackClass,
            KnownGoodRevision: knownGoodRevision,
            RollbackCommands: rollbackCommands,
            BlockingReasons: blockingReasons,
            Warnings: warnings);

        return true;
    }

    // -- AI DevOps explanation of the proposed PR operation (#57 AC#3) ---------

    // Build a structured, read-only explanation of the proposed metadata-release GitOps PR
    // operation directly from an already-built change set. This is a pure projection of the
    // change set's secret-free fields: it never re-reads the package, computes compatibility,
    // writes Git, opens a PR, or creates a server operation. Deterministic for a given change set.
    internal static MetadataChangeSetExplanation Explain(MetadataReleaseChangeSet changeSet)
    {
        string envs = changeSet.TargetEnvironments.Count == 0
            ? "the requested environment(s)"
            : string.Join(", ", changeSet.TargetEnvironments);

        Dictionary<string, int> filesByKind = changeSet.Files
            .GroupBy(file => file.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string fileKindSummary = filesByKind.Count == 0
            ? "no files"
            : string.Join(", ", filesByKind.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Value} {pair.Key}"));

        MetadataChangeSetExplanationSection prSection = new(
            Section: "pr-operation",
            Status: changeSet.Readiness,
            Detail: changeSet.Readiness == MetadataChangeSetReadiness.Blocked
                ? $"Proposed PR on branch `{changeSet.BranchName}` is blocked and should not be opened as-is."
                : $"Proposed PR on branch `{changeSet.BranchName}` would carry {changeSet.Files.Count} file(s) ({fileKindSummary}).",
            Findings:
            [
                $"Branch: {changeSet.BranchName}",
                $"PR title: {changeSet.PrTitle}",
                $"Files: {changeSet.Files.Count} ({fileKindSummary})",
                $"Targets: {envs}"
            ]);

        bool hasResources = changeSet.SemanticResources.Count > 0;
        MetadataChangeSetExplanationSection resourcesSection = new(
            Section: "semantic-resources",
            Status: hasResources ? MetadataChangeSetReadiness.Ready : MetadataChangeSetReadiness.Unknown,
            Detail: hasResources
                ? $"{changeSet.SemanticResources.Count} semantic Honua resource(s) change in this release."
                : "No semantic resources were listed in the release package.",
            // Resource kind/name/action are operator-supplied free text and can carry secrets
            // (e.g. an api_key=... fragment smuggled into a name); scrub before they land in the
            // explanation, the same redaction guarantee the YAML manifests and PR body already get.
            Findings: hasResources
                ? changeSet.SemanticResources
                    .Select(resource => $"{Redaction.Scrub(resource.Action)} {Redaction.Scrub(resource.Kind)}/{Redaction.Scrub(resource.Name)}")
                    .ToList()
                : ["No semantic resources to summarize."]);

        // Compatibility / script-coverage verdict is already folded into readiness + warnings +
        // blocking reasons on the change set; surface it as a derived section rather than
        // re-parsing the package.
        string compatibilityStatus = changeSet.Readiness switch
        {
            MetadataChangeSetReadiness.Blocked => MetadataChangeSetReadiness.Blocked,
            MetadataChangeSetReadiness.Warning => MetadataChangeSetReadiness.Warning,
            MetadataChangeSetReadiness.Ready => MetadataChangeSetReadiness.Ready,
            _ => MetadataChangeSetReadiness.Unknown
        };
        List<string> compatibilityFindings = [];
        compatibilityFindings.AddRange(changeSet.BlockingReasons);
        compatibilityFindings.AddRange(changeSet.Warnings);
        if (compatibilityFindings.Count == 0)
        {
            compatibilityFindings.Add("No compatibility blockers or advisory warnings were reported.");
        }

        MetadataChangeSetExplanationSection compatibilitySection = new(
            Section: "compatibility-and-coverage",
            Status: compatibilityStatus,
            Detail: changeSet.Readiness switch
            {
                MetadataChangeSetReadiness.Blocked => "Server compatibility/coverage evidence blocks this release.",
                MetadataChangeSetReadiness.Warning => "Server compatibility/coverage evidence carries advisory warnings.",
                MetadataChangeSetReadiness.Ready => "Server compatibility/coverage evidence is clean.",
                _ => "Compatibility/coverage evidence is unknown or unparseable."
            },
            Findings: compatibilityFindings);

        bool irreversible = changeSet.RollbackClassification == MetadataRollbackClass.Irreversible;
        List<string> rollbackFindings =
        [
            $"Classification: {changeSet.RollbackClassification}",
            $"Known-good revision: {changeSet.KnownGoodRevision ?? "unknown"}"
        ];
        rollbackFindings.AddRange(changeSet.RollbackCommands);
        MetadataChangeSetExplanationSection rollbackSection = new(
            Section: "rollback",
            Status: irreversible || changeSet.RollbackClassification == MetadataRollbackClass.Unknown
                ? MetadataChangeSetReadiness.Warning
                : MetadataChangeSetReadiness.Ready,
            Detail: irreversible
                ? "Rollback is classified irreversible; only a forward fix can recover this release."
                : changeSet.RollbackClassification == MetadataRollbackClass.NotRequired
                    ? "No rollback is required for this release."
                    : $"A {changeSet.RollbackClassification} rollback path is available; commands are approval-gated.",
            Findings: rollbackFindings);

        string verdict = changeSet.Readiness switch
        {
            MetadataChangeSetReadiness.Ready =>
                $"The proposed metadata-release PR for `{changeSet.Service}` {changeSet.DesiredRevision} -> {envs} is ready to open: compatibility evidence is clean and {changeSet.Files.Count} desired-state file(s) are prepared.",
            MetadataChangeSetReadiness.Warning =>
                $"The proposed metadata-release PR for `{changeSet.Service}` {changeSet.DesiredRevision} -> {envs} can proceed with advisory warnings; review them before opening the PR.",
            MetadataChangeSetReadiness.Blocked =>
                $"The proposed metadata-release PR for `{changeSet.Service}` {changeSet.DesiredRevision} -> {envs} is blocked by the supplied compatibility evidence and should not be opened as-is.",
            _ =>
                $"The proposed metadata-release PR for `{changeSet.Service}` {changeSet.DesiredRevision} -> {envs} has unknown readiness because the release-package evidence could not be fully interpreted."
        };
        string summary = $"{verdict} Rollback classification: {changeSet.RollbackClassification}.";

        return new MetadataChangeSetExplanation(
            ReleasePackageId: changeSet.ReleasePackageId,
            Service: changeSet.Service,
            DesiredRevision: changeSet.DesiredRevision,
            TargetEnvironments: changeSet.TargetEnvironments,
            Readiness: changeSet.Readiness,
            BranchName: changeSet.BranchName,
            Summary: summary,
            Sections: [prSection, resourcesSection, compatibilitySection, rollbackSection],
            SemanticResources: changeSet.SemanticResources,
            EvidenceLinks: changeSet.EvidenceLinks,
            RollbackClassification: changeSet.RollbackClassification,
            KnownGoodRevision: changeSet.KnownGoodRevision,
            RollbackCommands: changeSet.RollbackCommands,
            BlockingReasons: changeSet.BlockingReasons,
            Warnings: changeSet.Warnings);
    }

    // -- Deterministic Git artifacts -----------------------------------------

    internal static string BuildBranchName(string serviceToken, string revisionToken, string releaseId)
    {
        // Deterministic and idempotent: a re-run for the same release package yields the same
        // branch so PR updates target the existing branch rather than creating a new one. A
        // short hash of the release id disambiguates two releases that share service+revision.
        string suffix = ShortHash(releaseId);
        return $"honua-devops/metadata-release/{serviceToken}-{revisionToken}-{suffix}";
    }

    private static string BuildCommitMessage(
        string service,
        string desiredRevision,
        IReadOnlyList<string> environments,
        string releaseId)
    {
        StringBuilder builder = new();
        builder.Append($"Metadata release {service} {desiredRevision} -> {string.Join(", ", environments)}");
        builder.Append("\n\n");
        builder.Append($"Release-Package: {releaseId}\n");
        builder.Append($"Service: {service}\n");
        builder.Append($"Revision: {desiredRevision}\n");
        builder.Append($"Environments: {string.Join(", ", environments)}\n");
        builder.Append("Managed-By: honua-devops");
        return builder.ToString();
    }

    private static List<MetadataReleaseFile> BuildFiles(
        string service,
        string serviceToken,
        string desiredRevision,
        string revisionToken,
        IReadOnlyList<string> environments,
        string gitOpsTool,
        IReadOnlyList<MetadataResourceSummary> resources,
        string compatibilityStatus,
        int breakingChanges,
        int compatWarnings,
        int coveredScripts,
        int totalScripts,
        IReadOnlyList<string> uncoveredScripts,
        string rollbackClass,
        string? knownGoodRevision,
        IReadOnlyList<MetadataEvidenceLink> evidenceLinks)
    {
        List<MetadataReleaseFile> files = [];

        foreach (string environment in environments)
        {
            string environmentToken = NormalizeResourceToken(environment, "default");

            // PlatformRelease manifest for the metadata release, one per environment.
            files.Add(new MetadataReleaseFile(
                Path: $"desired-state/releases/{serviceToken}/{environmentToken}.platformrelease.yaml",
                Kind: "manifest",
                Content: RenderPlatformReleaseManifest(
                    serviceToken, service, environment, environmentToken, desiredRevision, revisionToken, gitOpsTool, resources)));

            // Environment overlay carrying the per-environment metadata binding annotations.
            files.Add(new MetadataReleaseFile(
                Path: $"desired-state/releases/{serviceToken}/overlays/{environmentToken}.overlay.yaml",
                Kind: "overlay",
                Content: RenderEnvironmentOverlay(
                    serviceToken, service, environment, environmentToken, desiredRevision, resources)));
        }

        // Optional data-scripts manifest: a coverage record only (the scripts themselves are
        // owned by the server package; we never inline script bodies, which can carry secrets).
        if (totalScripts > 0)
        {
            files.Add(new MetadataReleaseFile(
                Path: $"desired-state/releases/{serviceToken}/data-scripts.coverage.yaml",
                Kind: "scripts",
                Content: RenderScriptsCoverage(serviceToken, service, desiredRevision, coveredScripts, totalScripts, uncoveredScripts)));
        }

        // Rollback policy file derived from the rollback classification + known-good revision.
        files.Add(new MetadataReleaseFile(
            Path: $"desired-state/releases/{serviceToken}/rollback.policy.yaml",
            Kind: "rollback-policy",
            Content: RenderRollbackPolicy(serviceToken, service, desiredRevision, rollbackClass, knownGoodRevision)));

        // Validation evidence references (opaque pointers only; no payloads).
        files.Add(new MetadataReleaseFile(
            Path: $"desired-state/releases/{serviceToken}/validation-evidence.yaml",
            Kind: "evidence",
            Content: RenderValidationEvidence(
                serviceToken, service, desiredRevision, compatibilityStatus, breakingChanges, compatWarnings, evidenceLinks)));

        return files;
    }

    // -- Manifest rendering (allow-listed, secret-free) -----------------------

    private static string RenderPlatformReleaseManifest(
        string serviceToken,
        string service,
        string environment,
        string environmentToken,
        string desiredRevision,
        string revisionToken,
        string gitOpsTool,
        IReadOnlyList<MetadataResourceSummary> resources)
    {
        StringBuilder builder = new();
        AppendManifestHeader(builder, "PlatformRelease", $"{serviceToken}-{environmentToken}-{revisionToken}", environmentToken, service, environment);
        builder.Append("spec:\n");
        builder.Append($"  service: {Yaml(service)}\n");
        builder.Append($"  environment: {Yaml(environment)}\n");
        builder.Append($"  revision: {Yaml(desiredRevision)}\n");
        builder.Append("  action: sync\n");
        builder.Append($"  changeSummary: {Yaml($"Metadata release {desiredRevision} for {service}.")}\n");
        builder.Append($"  gitOpsTool: {Yaml(gitOpsTool)}\n");
        builder.Append("  semanticResources:\n");
        if (resources.Count == 0)
        {
            builder.Append("    []\n");
        }
        else
        {
            foreach (MetadataResourceSummary resource in resources)
            {
                builder.Append($"    - kind: {Yaml(resource.Kind)}\n");
                builder.Append($"      name: {Yaml(resource.Name)}\n");
                builder.Append($"      action: {Yaml(resource.Action)}\n");
            }
        }

        return builder.ToString();
    }

    private static string RenderEnvironmentOverlay(
        string serviceToken,
        string service,
        string environment,
        string environmentToken,
        string desiredRevision,
        IReadOnlyList<MetadataResourceSummary> resources)
    {
        StringBuilder builder = new();
        AppendManifestHeader(builder, "MetadataOverlay", $"{serviceToken}-{environmentToken}-overlay", environmentToken, service, environment);
        builder.Append("spec:\n");
        builder.Append($"  service: {Yaml(service)}\n");
        builder.Append($"  environment: {Yaml(environment)}\n");
        builder.Append($"  revision: {Yaml(desiredRevision)}\n");
        builder.Append("  bindings:\n");
        if (resources.Count == 0)
        {
            builder.Append("    []\n");
        }
        else
        {
            foreach (MetadataResourceSummary resource in resources)
            {
                builder.Append($"    - resource: {Yaml($"{resource.Kind}/{resource.Name}")}\n");
                builder.Append($"      environment: {Yaml(environment)}\n");
            }
        }

        return builder.ToString();
    }

    private static string RenderScriptsCoverage(
        string serviceToken,
        string service,
        string desiredRevision,
        int coveredScripts,
        int totalScripts,
        IReadOnlyList<string> uncoveredScripts)
    {
        StringBuilder builder = new();
        AppendManifestHeader(builder, "DataScriptCoverage", $"{serviceToken}-data-scripts", "control-plane", service, environment: null);
        builder.Append("spec:\n");
        builder.Append($"  service: {Yaml(service)}\n");
        builder.Append($"  revision: {Yaml(desiredRevision)}\n");
        builder.Append($"  covered: {coveredScripts}\n");
        builder.Append($"  total: {totalScripts}\n");
        builder.Append("  uncovered:\n");
        if (uncoveredScripts.Count == 0)
        {
            builder.Append("    []\n");
        }
        else
        {
            foreach (string script in uncoveredScripts)
            {
                builder.Append($"    - {Yaml(script)}\n");
            }
        }

        return builder.ToString();
    }

    private static string RenderRollbackPolicy(
        string serviceToken,
        string service,
        string desiredRevision,
        string rollbackClass,
        string? knownGoodRevision)
    {
        StringBuilder builder = new();
        AppendManifestHeader(builder, "RollbackPolicy", $"{serviceToken}-rollback", "control-plane", service, environment: null);
        builder.Append("spec:\n");
        builder.Append($"  service: {Yaml(service)}\n");
        builder.Append($"  revision: {Yaml(desiredRevision)}\n");
        builder.Append($"  classification: {Yaml(rollbackClass)}\n");
        builder.Append($"  knownGoodRevision: {Yaml(knownGoodRevision ?? "unknown")}\n");
        return builder.ToString();
    }

    private static string RenderValidationEvidence(
        string serviceToken,
        string service,
        string desiredRevision,
        string compatibilityStatus,
        int breakingChanges,
        int compatWarnings,
        IReadOnlyList<MetadataEvidenceLink> evidenceLinks)
    {
        StringBuilder builder = new();
        AppendManifestHeader(builder, "ValidationEvidence", $"{serviceToken}-validation", "control-plane", service, environment: null);
        builder.Append("spec:\n");
        builder.Append($"  service: {Yaml(service)}\n");
        builder.Append($"  revision: {Yaml(desiredRevision)}\n");
        builder.Append($"  compatibilityStatus: {Yaml(compatibilityStatus)}\n");
        builder.Append($"  breakingChanges: {breakingChanges}\n");
        builder.Append($"  warnings: {compatWarnings}\n");
        builder.Append("  evidenceRefs:\n");
        if (evidenceLinks.Count == 0)
        {
            builder.Append("    []\n");
        }
        else
        {
            foreach (MetadataEvidenceLink link in evidenceLinks)
            {
                builder.Append($"    - type: {Yaml(link.Type)}\n");
                builder.Append($"      reference: {Yaml(link.Reference)}\n");
            }
        }

        return builder.ToString();
    }

    private static void AppendManifestHeader(
        StringBuilder builder,
        string kind,
        string name,
        string @namespace,
        string service,
        string? environment)
    {
        builder.Append("apiVersion: honua.io/v1alpha1\n");
        builder.Append($"kind: {kind}\n");
        builder.Append("metadata:\n");
        builder.Append($"  name: {Yaml(name)}\n");
        builder.Append($"  namespace: {Yaml(@namespace)}\n");
        builder.Append("  labels:\n");
        builder.Append("    managed-by: honua-devops\n");
        builder.Append($"    service: {Yaml(service)}\n");
        if (environment is not null)
        {
            builder.Append($"    environment: {Yaml(NormalizeResourceToken(environment, "default"))}\n");
        }
    }

    // -- Rollback commands ----------------------------------------------------

    private static List<string> BuildRollbackCommands(
        string gitOpsTool,
        string service,
        IReadOnlyList<string> environments,
        string rollbackClass,
        string? knownGoodRevision)
    {
        List<string> commands = [];
        if (rollbackClass == MetadataRollbackClass.NotRequired)
        {
            return commands;
        }

        if (rollbackClass == MetadataRollbackClass.Irreversible)
        {
            commands.Add($"# rollback classified irreversible for {service}; forward-fix only, no automated revert available.");
            return commands;
        }

        string target = knownGoodRevision is null ? "<known-good-revision>" : ShellQuote(knownGoodRevision);
        foreach (string environment in environments)
        {
            commands.Add($"{gitOpsTool} rollback --service {ShellQuote(service)} --env {ShellQuote(environment)} --to-revision {target}");
        }

        if (rollbackClass == MetadataRollbackClass.Manual)
        {
            commands.Insert(0, "# manual rollback: requires operator approval before running the commands below.");
        }

        return commands;
    }

    // -- PR body --------------------------------------------------------------

    private static string BuildPrBody(
        string service,
        string desiredRevision,
        IReadOnlyList<string> environments,
        string releaseId,
        string readiness,
        IReadOnlyList<MetadataResourceSummary> resources,
        string compatibilityStatus,
        int breakingChanges,
        int compatWarnings,
        int coveredScripts,
        int totalScripts,
        IReadOnlyList<string> uncoveredScripts,
        string rollbackClass,
        string? knownGoodRevision,
        IReadOnlyList<string> rollbackCommands,
        IReadOnlyList<MetadataEvidenceLink> evidenceLinks,
        IReadOnlyList<string> blockingReasons,
        IReadOnlyList<string> warnings)
    {
        StringBuilder builder = new();
        builder.Append($"## Metadata release: {service} {desiredRevision}\n\n");
        builder.Append($"- Release package: `{releaseId}`\n");
        builder.Append($"- Service: `{service}`\n");
        builder.Append($"- Desired revision: `{desiredRevision}`\n");
        builder.Append($"- Target environments: {string.Join(", ", environments.Select(environment => $"`{environment}`"))}\n");
        builder.Append($"- Readiness: **{readiness}**\n\n");

        builder.Append("### Semantic resources\n\n");
        if (resources.Count == 0)
        {
            builder.Append("_No semantic resources were listed in the release package._\n\n");
        }
        else
        {
            foreach (MetadataResourceSummary resource in resources)
            {
                // Resource kind/name are operator-supplied free text and can carry secrets
                // (e.g. an api_key=... fragment smuggled into a name); scrub before they land
                // in the PR body, the same redaction guarantee the YAML manifests already get.
                builder.Append($"- `{Redaction.Scrub(resource.Kind)}/{Redaction.Scrub(resource.Name)}` ({Redaction.Scrub(resource.Action)})\n");
            }

            builder.Append('\n');
        }

        builder.Append("### Compatibility\n\n");
        builder.Append($"- Status: `{compatibilityStatus}` ({breakingChanges} breaking, {compatWarnings} warning)\n\n");

        builder.Append("### Data script coverage\n\n");
        builder.Append(totalScripts > 0
            ? $"- {coveredScripts}/{totalScripts} covered{(uncoveredScripts.Count > 0 ? $"; uncovered: {string.Join(", ", uncoveredScripts.Select(script => $"`{script}`"))}" : string.Empty)}\n\n"
            : "- No data scripts in this release.\n\n");

        builder.Append("### Rollback\n\n");
        builder.Append($"- Classification: `{rollbackClass}`\n");
        builder.Append($"- Known-good revision: `{knownGoodRevision ?? "unknown"}`\n");
        if (rollbackCommands.Count > 0)
        {
            builder.Append("\n```sh\n");
            foreach (string command in rollbackCommands)
            {
                builder.Append(command);
                builder.Append('\n');
            }

            builder.Append("```\n");
        }

        builder.Append('\n');
        builder.Append("### Evidence\n\n");
        if (evidenceLinks.Count == 0)
        {
            builder.Append("_No evidence references were supplied with the release package._\n");
        }
        else
        {
            foreach (MetadataEvidenceLink link in evidenceLinks)
            {
                builder.Append($"- {link.Type}: `{link.Reference}` — {link.Summary}\n");
            }
        }

        if (blockingReasons.Count > 0)
        {
            builder.Append("\n### Blocking\n\n");
            foreach (string reason in blockingReasons)
            {
                builder.Append($"- {reason}\n");
            }
        }

        if (warnings.Count > 0)
        {
            builder.Append("\n### Warnings\n\n");
            foreach (string warning in warnings)
            {
                builder.Append($"- {warning}\n");
            }
        }

        builder.Append("\n---\n");
        builder.Append("Generated by honua-devops (read-only). This PR is a desired-state change set only; ");
        builder.Append("merge and reconcile run through the governed GitOps/approval path.\n");
        return builder.ToString();
    }

    // -- Release-package reading ----------------------------------------------

    private static IReadOnlyList<MetadataResourceSummary> ReadSemanticResources(JsonElement root)
    {
        if (!TryGetArray(root, out JsonElement array, "semanticResources", "resources", "metadataResources"))
        {
            return [];
        }

        List<MetadataResourceSummary> resources = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string kind = DeploymentInputs.SanitizeFreeText(TryGetString(element, "kind", "type"), "Resource");
            string name = DeploymentInputs.SanitizeFreeText(TryGetString(element, "name", "id"), "unnamed");
            string action = NormalizeToolToken(TryGetString(element, "action", "operation"), "upsert");
            string? detail = TryGetString(element, "detail", "summary");
            resources.Add(new MetadataResourceSummary(
                kind,
                name,
                action,
                detail is null ? null : DeploymentInputs.SanitizeFreeText(detail, string.Empty)));
        }

        return resources;
    }

    private static (string Status, int Breaking, int Warnings, List<string> Reasons) ReadCompatibility(JsonElement root)
    {
        if (!TryGetObject(root, out JsonElement report, "compatibility", "compatibilityReport"))
        {
            return (MetadataChangeSetReadiness.Unknown, 0, 0, []);
        }

        string raw = (TryGetString(report, "status", "result") ?? "unknown").ToLowerInvariant();
        int breaking = ReadInt(report, "breakingChanges", "breaking");
        int warnings = ReadInt(report, "warnings", "warning");
        List<string> reasons = ReadStringList(report, "findings", "notes");

        string status = raw switch
        {
            "incompatible" or "fail" or "failed" or "blocked" => MetadataChangeSetReadiness.Blocked,
            "compatible" or "ok" or "pass" or "passed" => breaking > 0
                ? MetadataChangeSetReadiness.Blocked
                : warnings > 0 ? MetadataChangeSetReadiness.Warning : MetadataChangeSetReadiness.Ready,
            "unknown" or "" => MetadataChangeSetReadiness.Unknown,
            _ => breaking > 0 ? MetadataChangeSetReadiness.Blocked : warnings > 0 ? MetadataChangeSetReadiness.Warning : MetadataChangeSetReadiness.Ready
        };

        return (status, breaking, warnings, reasons);
    }

    private static (int Covered, int Total, List<string> Uncovered) ReadScriptCoverage(JsonElement root)
    {
        if (!TryGetObject(root, out JsonElement coverage, "scriptCoverage", "dataScripts", "scripts", "coverage"))
        {
            return (0, 0, []);
        }

        int covered = ReadInt(coverage, "covered", "coveredScripts");
        int total = ReadInt(coverage, "total", "totalScripts", "required");
        List<string> uncovered = ReadStringList(coverage, "uncovered", "missing", "gaps");
        return (covered, total, uncovered);
    }

    private static (string Classification, string? KnownGoodRevision) ReadRollback(JsonElement root)
    {
        if (!TryGetObject(root, out JsonElement rollback, "rollbackPolicy", "rollback"))
        {
            return (MetadataRollbackClass.Unknown, null);
        }

        string raw = (TryGetString(rollback, "classification", "class", "kind") ?? "unknown").ToLowerInvariant();
        bool required = ReadBool(rollback, "required", "rollbackRequired");
        string? knownGood = TryGetString(rollback, "knownGoodRevision", "priorRevision", "previousRevision", "to");
        if (knownGood is not null)
        {
            knownGood = ValidateRevisionOrFallback(knownGood, knownGood);
        }

        string classification = raw switch
        {
            "automatic" or "auto" => MetadataRollbackClass.Automatic,
            "manual" => MetadataRollbackClass.Manual,
            "irreversible" or "forward-only" or "forward-fix" => MetadataRollbackClass.Irreversible,
            "not-required" or "none" => MetadataRollbackClass.NotRequired,
            _ => required ? MetadataRollbackClass.Manual : MetadataRollbackClass.Unknown
        };

        return (classification, knownGood);
    }

    private static List<MetadataEvidenceLink> BuildEvidenceLinks(JsonElement root, string releaseId)
    {
        List<MetadataEvidenceLink> links =
        [
            new MetadataEvidenceLink("release-package", $"release-package:{releaseId}", "Server-validated metadata release package.")
        ];

        if (TryGetObject(root, out JsonElement report, "compatibility", "compatibilityReport"))
        {
            string? reportRef = TryGetString(report, "reportId", "id", "ref");
            links.Add(new MetadataEvidenceLink(
                "compatibility-report",
                reportRef is null ? $"compatibility-report:{releaseId}" : $"compatibility-report:{DeploymentInputs.SanitizeFreeText(reportRef, releaseId)}",
                "Server compatibility report reference."));
        }

        if (TryGetObject(root, out _, "scriptCoverage", "dataScripts", "scripts", "coverage"))
        {
            links.Add(new MetadataEvidenceLink(
                "script-coverage",
                $"script-coverage:{releaseId}",
                "Data script coverage reference."));
        }

        return links;
    }

    private static string[] ReadEnvironments(JsonElement root)
    {
        List<string> environments = ReadStringList(root, "targetEnvironments", "environments");
        List<string> tokens = environments
            .Select(environment => NormalizeResourceToken(environment, string.Empty))
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tokens.Count == 0 ? ["staging"] : tokens.ToArray();
    }

    // -- Sanitization / formatting helpers ------------------------------------

    private static string SanitizeService(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        try
        {
            return DeploymentInputs.ValidateServiceName(value);
        }
        catch (InvalidOperationException)
        {
            return NormalizeResourceToken(value, "unknown");
        }
    }

    private static string ValidateRevisionOrFallback(string? value, string fallback)
    {
        try
        {
            return DeploymentInputs.ValidateRevision(DeploymentInputs.Normalize(value, fallback), "revision");
        }
        catch (InvalidOperationException)
        {
            return NormalizeResourceToken(value, NormalizeResourceToken(fallback, "head"));
        }
    }

    private static string NormalizeToolToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string token = NormalizeResourceToken(value, fallback);
        return token.Length == 0 ? fallback : token;
    }

    private static string NormalizeResourceToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        StringBuilder builder = new();
        bool appendHyphen = false;
        foreach (char character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                appendHyphen = true;
                continue;
            }

            if (appendHyphen)
            {
                builder.Append('-');
                appendHyphen = false;
            }
        }

        string normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    // Render a scalar into a YAML value: redaction-scrubbed (no secrets leak into Git) and
    // always single-quoted with YAML escaping so control/special characters cannot break the
    // document or smuggle structure.
    private static string Yaml(string value)
    {
        string scrubbed = Redaction.Scrub(value ?? string.Empty);
        string collapsed = new string(scrubbed
            .Where(character => !char.IsControl(character) || character == '\t')
            .Select(character => character == '\t' ? ' ' : character)
            .ToArray())
            .Trim();
        return $"'{collapsed.Replace("'", "''")}'";
    }

    private static string ShellQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        bool safe = value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':' or '@');
        return safe ? value : $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // -- JSON parsing helpers (self-contained) --------------------------------

    internal static bool TryParse(string? json, out JsonElement root, out string? error)
    {
        root = default;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Release-package document was empty.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Release-package document must be a JSON object.";
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Release-package document is not valid JSON: {Redaction.Scrub(exception.Message)}";
            return false;
        }
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    string? value = property.Value.GetString();
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                case JsonValueKind.Number:
                    return property.Value.GetRawText();
            }
        }

        return null;
    }

    private static int ReadInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value.GetArrayLength();
            }
        }

        return 0;
    }

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    string? text = property.Value.GetString();
                    return text is not null
                        && (text.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || text.Equals("yes", StringComparison.OrdinalIgnoreCase));
            }
        }

        return false;
    }

    private static List<string> ReadStringList(JsonElement element, params string[] names)
    {
        List<string> values = [];
        if (element.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (JsonElement item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string sanitized = DeploymentInputs.SanitizeFreeText(item.GetString(), string.Empty);
                            if (sanitized.Length > 0)
                            {
                                values.Add(sanitized);
                            }
                        }
                    }

                    return values;
                case JsonValueKind.String:
                    string single = DeploymentInputs.SanitizeFreeText(property.Value.GetString(), string.Empty);
                    if (single.Length > 0)
                    {
                        values.Add(single);
                    }

                    return values;
            }
        }

        return values;
    }

    private static bool TryGetObject(JsonElement element, out JsonElement value, params string[] names)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetArray(JsonElement element, out JsonElement value, params string[] names)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
