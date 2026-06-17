using System.Text.Json;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Operations.GitOps;

internal static class GitOpsPlanner
{
    private static readonly IReadOnlyDictionary<string, GitOpsTransitionContract> TransitionContracts =
        new Dictionary<string, GitOpsTransitionContract>(StringComparer.OrdinalIgnoreCase)
        {
            ["plan"] = new("desired-revision", "planned", false),
            ["diff"] = new("planned", "diff-reviewed", false),
            ["sync"] = new("diff-reviewed", "applied", true),
            ["status"] = new("applied", "status-read", false),
            ["drift"] = new("status-read", "drift-checked", false),
            ["pause"] = new("reconciling", "paused", true),
            ["resume"] = new("paused", "reconciling", true),
            ["approve"] = new("approval-requested", "approved", true),
            ["promote"] = new("approved", "promoted", true),
            ["rollback"] = new("applied", "rolled-back", true)
        };

    private static readonly string[] SupportedOperations =
    [
        "plan",
        "diff",
        "sync",
        "status",
        "drift",
        "pause",
        "resume",
        "approve",
        "promote",
        "rollback"
    ];

    internal static GitOpsPlan Build(
        string service,
        IReadOnlyList<string> environments,
        string desiredRevision,
        string requestedAction,
        string gitOpsTool,
        bool dryRun,
        string policyGate,
        GitOpsDeployBackendResult backendResult,
        ReleaseOrchestrationPlan releaseOrchestration,
        ServiceBundleReconciliationPlan serviceBundleReconciliation)
    {
        string actualStateSource = backendResult.ExportResult.IsSuccess
            ? "manifest-export"
            : "manifest-export-unavailable";
        IReadOnlyDictionary<string, string> exportedRevisions = ExtractActualRevisions(
            backendResult.ExportPayload,
            service);

        GitOpsEnvironmentPlan[] environmentPlans = environments
            .Select(environment =>
            {
                string actualRevision = exportedRevisions.TryGetValue(environment, out string? revision) &&
                    !string.IsNullOrWhiteSpace(revision)
                    ? revision
                    : "unknown";
                bool serviceStateKnown = !actualRevision.Equals("unknown", StringComparison.OrdinalIgnoreCase);
                string diffStatus = actualRevision.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    ? "actual-state-pending"
                    : actualRevision.Equals(desiredRevision, StringComparison.OrdinalIgnoreCase)
                        ? "in-sync"
                        : "revision-diff";

                ServiceBundleReconciliation.ServiceBundleDriftScope? serviceStateScope = serviceBundleReconciliation.DriftScopes
                    .FirstOrDefault(scope => scope.Scope.Equals("service-state", StringComparison.OrdinalIgnoreCase));

                GitOpsDriftStatus[] drift =
                [
                    new(
                        Scope: "infra",
                        Status: "check-required",
                        Detail: "Run target adapter drift detection against the Terraform-selected runtime."),
                    new(
                        Scope: "release",
                        Status: diffStatus == "revision-diff" ? "revision-drift" : diffStatus == "in-sync" ? "no-release-drift" : "unknown",
                        Detail: actualRevision == "unknown"
                            ? "Manifest export did not expose a comparable current revision."
                            : $"Desired revision `{desiredRevision}` compared against exported revision `{actualRevision}`."),
                    // Service-state drift now carries the REAL verdict computed by the
                    // ServiceBundleReconciliationPlanner (drift-detected/no-drift/unreconcilable),
                    // not the old exported/export-required stub.
                    new(
                        Scope: "service-state",
                        Status: serviceStateScope?.DriftVerdict ?? (serviceStateKnown ? "exported" : "export-required"),
                        Detail: serviceStateScope?.DriftDetail ?? serviceBundleReconciliation.CurrentStateSummary)
                ];

                return new GitOpsEnvironmentPlan(
                    Environment: environment,
                    ActualRevision: actualRevision,
                    DesiredRevision: desiredRevision,
                    DiffStatus: diffStatus,
                    GateStatus: BuildEnvironmentGateStatus(environment, policyGate, releaseOrchestration),
                    Drift: drift,
                    Commands: BuildCommands(gitOpsTool, service, environment, desiredRevision, dryRun, releaseOrchestration.PromotionMode));
            })
            .ToArray();

        GitOpsStateTransitionPlan[] transitions = environmentPlans
            .SelectMany(environment => environment.Commands.Select(command =>
            {
                GitOpsTransitionContract contract = BuildTransitionContract(command.Operation, dryRun);
                return new GitOpsStateTransitionPlan(
                    Operation: command.Operation,
                    Environment: environment.Environment,
                    FromState: contract.FromState,
                    ToState: contract.ToState,
                    MutatesState: !dryRun && contract.MutatesState,
                    RequiresApproval: command.RequiresApproval,
                    Enabled: !command.RequiresApproval || releaseOrchestration.PromotionMode == "gated-promotion",
                    Summary: command.Summary,
                    SuggestedCommand: command.Command,
                    RequiredChecks: command.RequiresApproval
                        ? ["approval-record", "lower-env-evidence"]
                        : ["manifest-diff", "gitops-status"]);
            }))
            .ToArray();

        string diffSummary = BuildDiffSummary(environmentPlans, desiredRevision);
        string driftSummary = BuildDriftSummary(environmentPlans);

        return new GitOpsPlan(
            Engine: gitOpsTool,
            RequestedAction: requestedAction,
            EffectiveAction: dryRun ? "dry-run" : requestedAction,
            ActualStateSource: actualStateSource,
            DiffSummary: diffSummary,
            DriftSummary: driftSummary,
            GateStatus: dryRun ? "plan-only" : policyGate,
            SupportedOperations: SupportedOperations,
            RequiredEvidence: BuildRequiredEvidence(releaseOrchestration, serviceBundleReconciliation),
            Environments: environmentPlans,
            StateTransitions: transitions);
    }

    internal static string BuildCurrentRevisionSummary(GitOpsPlan plan)
    {
        return string.Join(", ", plan.Environments.Select(environment => $"{environment.Environment}={environment.ActualRevision}"));
    }

    // Per-environment metadata target status used to flag, on each gitops environment plan, whether
    // the metadata release change set actually targets that environment ("in-scope") or not
    // ("not-targeted"). Kept here so the vocabulary lives next to the summarizer that emits it.
    internal static class MetadataTargetStatus
    {
        internal const string InScope = "in-scope";
        internal const string NotTargeted = "not-targeted";
    }

    // Project a metadata release change set onto an existing gitops plan (issue #57, AC#4). This is
    // additive and read-only: it attaches a GitOpsMetadataReleaseSummary and tags each environment
    // plan with a MetadataTargetStatus by intersecting the change set's target environments with the
    // plan's environments. The deploy-tool path that builds the base plan is untouched.
    internal static GitOpsPlan AttachMetadataRelease(GitOpsPlan plan, MetadataReleaseChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(changeSet);

        HashSet<string> targeted = new(changeSet.TargetEnvironments, StringComparer.OrdinalIgnoreCase);

        GitOpsEnvironmentPlan[] environments = plan.Environments
            .Select(environment => environment with
            {
                MetadataTargetStatus = targeted.Contains(environment.Environment)
                    ? MetadataTargetStatus.InScope
                    : MetadataTargetStatus.NotTargeted
            })
            .ToArray();

        return plan with
        {
            Environments = environments,
            MetadataRelease = SummarizeMetadataRelease(changeSet)
        };
    }

    // Summarize a metadata release change set into the plan-facing projection. Compatibility status,
    // breaking/warning counts, and script coverage are not stored verbatim on the change set; they
    // are derived from the readiness verdict, the blocking reasons, and the warnings the builder
    // recorded so the projection stays faithful without re-reading the release package.
    internal static GitOpsMetadataReleaseSummary SummarizeMetadataRelease(MetadataReleaseChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);

        string compatibilityStatus = changeSet.Readiness switch
        {
            MetadataChangeSetReadiness.Blocked => "incompatible",
            MetadataChangeSetReadiness.Warning => "compatible-with-warnings",
            MetadataChangeSetReadiness.Ready => "compatible",
            _ => "unknown"
        };

        int breakingChanges = CountBreakingChanges(changeSet.BlockingReasons);
        string scriptCoverage = DeriveScriptCoverage(changeSet.Warnings);

        return new GitOpsMetadataReleaseSummary(
            ReleasePackageId: changeSet.ReleasePackageId,
            Readiness: changeSet.Readiness,
            SemanticResources: changeSet.SemanticResources,
            CompatibilityStatus: compatibilityStatus,
            BreakingChanges: breakingChanges,
            Warnings: changeSet.Warnings.Count,
            ScriptCoverage: scriptCoverage,
            RollbackClassification: changeSet.RollbackClassification,
            KnownGoodRevision: changeSet.KnownGoodRevision,
            BlockingReasons: changeSet.BlockingReasons);
    }

    private static int CountBreakingChanges(IReadOnlyList<string> blockingReasons)
    {
        // The builder records breaking changes as "... flags N breaking change(s); ...". Recover the
        // count when present; otherwise fall back to "1 if anything is blocking, else 0" so the
        // projection never under-reports a blocked release.
        foreach (string reason in blockingReasons)
        {
            int flagsIndex = reason.IndexOf("flags ", StringComparison.OrdinalIgnoreCase);
            int breakingIndex = reason.IndexOf(" breaking", StringComparison.OrdinalIgnoreCase);
            if (flagsIndex >= 0 && breakingIndex > flagsIndex)
            {
                string candidate = reason.Substring(flagsIndex + 6, breakingIndex - (flagsIndex + 6)).Trim();
                if (int.TryParse(candidate, out int count))
                {
                    return count;
                }
            }
        }

        return blockingReasons.Count > 0 ? 1 : 0;
    }

    private static string DeriveScriptCoverage(IReadOnlyList<string> warnings)
    {
        // The builder records script-coverage gaps as "C/T data script(s) covered; ...". Surface the
        // ratio when present; absence of a coverage warning means full (or no required) coverage, so
        // report "covered".
        foreach (string warning in warnings)
        {
            int coveredIndex = warning.IndexOf(" data script", StringComparison.OrdinalIgnoreCase);
            if (coveredIndex <= 0)
            {
                continue;
            }

            string prefix = warning.Substring(0, coveredIndex).Trim();
            if (prefix.Contains('/') && prefix.All(character => char.IsDigit(character) || character == '/'))
            {
                return prefix;
            }
        }

        return "covered";
    }

    private static IReadOnlyList<string> BuildRequiredEvidence(
        ReleaseOrchestrationPlan releaseOrchestration,
        ServiceBundleReconciliationPlan serviceBundleReconciliation)
    {
        return
        [
            "manifest-diff",
            "gitops-status",
            "gitops-drift:infra",
            "gitops-drift:release",
            "gitops-drift:service-state",
            ..releaseOrchestration.PromotionPolicy.RequiredEvidence,
            ..releaseOrchestration.RollbackPolicy.RequiredEvidence,
            ..releaseOrchestration.RollbackSemantics
                .SelectMany(item => item.EvidenceRequirements)
                .ToArray(),
            ..serviceBundleReconciliation.DriftScopes
                .SelectMany(scope => scope.EvidenceRequirements)
                .ToArray()
        ];
    }

    private static string BuildEnvironmentGateStatus(
        string environment,
        string policyGate,
        ReleaseOrchestrationPlan releaseOrchestration)
    {
        if (environment.Equals("prod", StringComparison.OrdinalIgnoreCase) &&
            releaseOrchestration.PromotionMode == "gated-promotion")
        {
            return releaseOrchestration.PromotionPolicy.Gate;
        }

        return policyGate;
    }

    private static GitOpsCommandPlan[] BuildCommands(
        string gitOpsTool,
        string service,
        string environment,
        string desiredRevision,
        bool dryRun,
        string promotionMode)
    {
        bool promotionRequiresApproval = promotionMode == "gated-promotion";

        return
        [
            new("plan", "Render desired state and candidate actions.", $"{gitOpsTool} plan --service {ShellQuote(service)} --env {ShellQuote(environment)} --revision {ShellQuote(desiredRevision)}", false),
            new("diff", "Compare desired and actual state for this environment.", $"{gitOpsTool} diff --service {ShellQuote(service)} --env {ShellQuote(environment)} --revision {ShellQuote(desiredRevision)}", false),
            new("sync", dryRun ? "Preview the sync path without mutating state." : "Apply the desired release to the environment.", $"{gitOpsTool} sync --service {ShellQuote(service)} --env {ShellQuote(environment)} --revision {ShellQuote(desiredRevision)}", false),
            new("status", "Read the current release and gate state.", $"{gitOpsTool} status --service {ShellQuote(service)} --env {ShellQuote(environment)}", false),
            new("drift", "Report infra, release, and service-state drift.", $"{gitOpsTool} drift --service {ShellQuote(service)} --env {ShellQuote(environment)}", false),
            new("pause", "Pause reconciliation while evidence or rollback decisions are gathered.", $"{gitOpsTool} pause --service {ShellQuote(service)} --env {ShellQuote(environment)}", promotionRequiresApproval),
            new("resume", "Resume reconciliation after pause or approval.", $"{gitOpsTool} resume --service {ShellQuote(service)} --env {ShellQuote(environment)}", promotionRequiresApproval),
            new("approve", "Record approval for gated promotion or execute flow.", $"{gitOpsTool} approve --service {ShellQuote(service)} --env {ShellQuote(environment)} --revision {ShellQuote(desiredRevision)}", true),
            new("promote", "Promote the validated revision to the next environment.", $"{gitOpsTool} promote --service {ShellQuote(service)} --env {ShellQuote(environment)} --revision {ShellQuote(desiredRevision)}", promotionRequiresApproval),
            new("rollback", "Return to the last known-good revision or checkpoint.", $"{gitOpsTool} rollback --service {ShellQuote(service)} --env {ShellQuote(environment)} --to-revision <known-good>", promotionRequiresApproval)
        ];
    }

    private static string BuildDiffSummary(
        IReadOnlyList<GitOpsEnvironmentPlan> environmentPlans,
        string desiredRevision)
    {
        int exactMatches = environmentPlans.Count(environment => environment.ActualRevision.Equals(desiredRevision, StringComparison.OrdinalIgnoreCase));
        int revisionDiffs = environmentPlans.Count(environment => environment.DiffStatus == "revision-diff");
        int unknowns = environmentPlans.Count(environment => environment.DiffStatus == "actual-state-pending");

        return $"desired={desiredRevision}; in-sync={exactMatches}; revision-diff={revisionDiffs}; actual-state-pending={unknowns}";
    }

    private static string BuildDriftSummary(IReadOnlyList<GitOpsEnvironmentPlan> environmentPlans)
    {
        int infraChecks = environmentPlans.SelectMany(environment => environment.Drift).Count(drift => drift.Scope == "infra");
        int releaseChecks = environmentPlans.SelectMany(environment => environment.Drift).Count(drift => drift.Scope == "release");
        int serviceChecks = environmentPlans.SelectMany(environment => environment.Drift).Count(drift => drift.Scope == "service-state");
        return $"infra-checks={infraChecks}; release-checks={releaseChecks}; service-state-checks={serviceChecks}";
    }

    private static GitOpsTransitionContract BuildTransitionContract(string operation, bool dryRun)
    {
        if (!TransitionContracts.TryGetValue(operation, out GitOpsTransitionContract? contract))
        {
            return new("unknown", "unknown", false);
        }

        if (dryRun && operation.Equals("sync", StringComparison.OrdinalIgnoreCase))
        {
            return contract with { ToState = "sync-preview" };
        }

        return contract;
    }

    private static IReadOnlyDictionary<string, string> ExtractActualRevisions(
        JsonDocument? exportPayload,
        string service)
    {
        if (exportPayload is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        JsonElement root = exportPayload.RootElement;
        JsonElement resources = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("resources", out JsonElement resourcesElement))
        {
            resources = resourcesElement;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("items", out JsonElement itemsElement))
        {
            resources = itemsElement;
        }

        if (resources.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string> revisions = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string? resourceService = ReadNestedString(resource, "spec", "deployment", "service")
                ?? ReadNestedString(resource, "metadata", "labels", "service");
            if (!string.Equals(resourceService, service, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? environment = ReadNestedString(resource, "spec", "deployment", "environment")
                ?? ReadNestedString(resource, "metadata", "namespace");
            string? revision = ReadNestedString(resource, "spec", "deployment", "revision")
                ?? ReadNestedString(resource, "metadata", "annotations", "honua.devops/revision");
            if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(revision))
            {
                continue;
            }

            revisions[environment] = revision;
        }

        return revisions;
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
    }

    private static string ShellQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        bool isSafeToken = value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '-' or '_' or '.' or '/' or ':' or '@');
        if (isSafeToken)
        {
            return value;
        }

        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private sealed record GitOpsTransitionContract(
        string FromState,
        string ToState,
        bool MutatesState);
}
