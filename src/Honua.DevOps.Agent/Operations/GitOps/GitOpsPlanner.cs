using System.Text.Json;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Operations.GitOps;

internal static class GitOpsPlanner
{
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
                    new(
                        Scope: "service-state",
                        Status: serviceStateKnown ? "exported" : "export-required",
                        Detail: serviceStateKnown
                            ? serviceBundleReconciliation.CurrentStateSummary
                            : $"Evaluate {string.Join(", ", serviceBundleReconciliation.DriftScopes.Select(scope => scope.Scope))} drift from control-plane exports.")
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
            .SelectMany(environment => environment.Commands.Select(command => new GitOpsStateTransitionPlan(
                Operation: command.Operation,
                Environment: environment.Environment,
                Enabled: !command.RequiresApproval || releaseOrchestration.PromotionMode == "gated-promotion",
                Summary: command.Summary,
                SuggestedCommand: command.Command,
                RequiredChecks: command.RequiresApproval
                    ? ["approval-record", "lower-env-evidence"]
                    : ["manifest-diff", "gitops-status"])))
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
}
