using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.ConsoleBridge;

// Console-facing, read-only release-package explanation surface (issue #58).
//
// Console hands the bridge a release-package evidence document — the server-computed
// compatibility report, script coverage, PR preview, promotion gates, and rollback plan —
// and gets back a structured ReleaseExplanation: per-section status, promotion gates,
// required approvals, residual risk, a rollback classification, and a single human-readable
// summary. Console renders all of it without parsing prose or scraping Git/CI.
//
// Boundaries (issue #58 "Out of Scope"):
//   * This does NOT compute compatibility — it interprets the server-supplied report.
//   * It is read-only in "explanation" mode and never creates a server operation. In
//     "proposal" mode it still only emits a governed, approval-required handoff suggestion;
//     it never submits, applies, or rolls back.
//   * All free text passing through is redaction-scrubbed; no caller-supplied value is
//     surfaced verbatim without going through Redaction.Scrub.
internal sealed class ReleasePackageExplainer(BackendConfiguration configuration)
{
    private const string ExplanationMode = "explanation";
    private const string ProposalMode = "proposal";

    [Description(
        "Explain a release package for Honua Console without computing compatibility or scraping Git/CI. " +
        "Input is the release-package evidence JSON document (server compatibility report, script coverage, " +
        "PR preview, promotion gates, and rollback plan). Returns a structured, evidence-linked explanation: " +
        "per-section status, promotion gates, required approvals, residual risk, a rollback classification, " +
        "and a single human-readable summary, plus an overall readiness of ready/warning/blocked/unknown/" +
        "rollback-required. mode is 'explanation' (read-only, default) or 'proposal' (governed handoff only; " +
        "still never executes). correlationId links the Console action to the server release package and any " +
        "downstream PR/CI/GitOps operation. Returns an unknown projection when the document cannot be parsed.")]
    public Task<OperationResponse> ExplainReleasePackageAsync(
        string releasePackageJson,
        string mode,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedMode = NormalizeMode(mode);
        string normalizedCorrelationId = NormalizeCorrelationId(correlationId);
        string timestamp = Timestamp();

        if (!TryParse(releasePackageJson, out JsonElement root, out string? parseError))
        {
            return Task.FromResult(BuildUnknown(normalizedMode, normalizedCorrelationId, parseError, timestamp));
        }

        string releaseId = DeploymentInputs.SanitizeFreeText(
            Redaction.Scrub(TryGetString(root, "releaseId", "release_id", "id")), "unknown-release");
        string service = SanitizeService(Redaction.Scrub(TryGetString(root, "service", "serviceName")));
        string desiredRevision = DeploymentInputs.SanitizeFreeText(
            Redaction.Scrub(TryGetString(root, "desiredRevision", "desired_revision", "revision")), "unknown");
        string? operationId = NormalizeOperationId(TryGetString(root, "operationId", "operation_id"));
        string[] environments = ReadStringArray(root, "targetEnvironments", "environments");

        List<EvidenceRef> evidence = [];
        List<ReleaseExplanationSection> sections =
        [
            BuildCompatibilitySection(root, timestamp, evidence),
            BuildScriptCoverageSection(root, timestamp, evidence),
            BuildPrPreviewSection(root, timestamp, evidence),
            BuildPromotionGatesSection(root, timestamp, evidence, out List<PromotionGate> gates),
            BuildRollbackSection(root, timestamp, evidence, out string rollbackClass)
        ];

        List<string> requiredApprovals = ReadStringList(root, "requiredApprovals", "approvals");
        List<string> residualRisks = ReadStringList(root, "residualRisks", "residualRisk", "risks");

        string readiness = DeriveReadiness(sections, gates, rollbackClass);
        List<string> blockingReasons = CollectBlockingReasons(sections, gates, rollbackClass, readiness);

        if (operationId is not null)
        {
            evidence.Add(ServerOperationEvidence(operationId, timestamp));
        }

        List<WorkflowLink> links = [SelfLink(operationId)];
        if (operationId is not null)
        {
            links.Add(ServerOperationLink(operationId));
        }

        List<SuggestedAction> suggestedActions = BuildSuggestedActions(
            normalizedMode, readiness, rollbackClass, operationId);

        string explanationId = $"release-explanation:{ShortHash($"{releaseId}:{service}:{desiredRevision}:{normalizedCorrelationId}")}";
        string summary = BuildSummary(service, environments, desiredRevision, readiness, rollbackClass, gates);

        ReleaseExplanation explanation = new(
            ExplanationId: explanationId,
            OperationId: operationId,
            CorrelationId: normalizedCorrelationId,
            Mode: normalizedMode,
            ReleaseId: releaseId,
            Service: service,
            TargetEnvironments: environments,
            DesiredRevision: desiredRevision,
            Readiness: readiness,
            Summary: summary,
            Sections: sections,
            PromotionGates: gates,
            RequiredApprovals: requiredApprovals,
            ResidualRisks: residualRisks,
            RollbackClassification: rollbackClass,
            Evidence: evidence,
            SuggestedActions: suggestedActions,
            WorkflowLinks: links,
            BlockingReasons: blockingReasons,
            CreatedAt: timestamp);

        return Task.FromResult(BuildResponse(explanation));
    }

    private OperationResponse BuildResponse(ReleaseExplanation explanation)
    {
        ConsoleBridgeProjection projection = new("release-explanation", ReleaseExplanation: explanation);

        List<string> findings =
        [
            $"Release: {explanation.ReleaseId} ({explanation.Service} -> {string.Join(", ", explanation.TargetEnvironments.DefaultIfEmpty("unspecified"))} @ {explanation.DesiredRevision})",
            $"Readiness: {explanation.Readiness}",
            $"Mode: {explanation.Mode}",
            $"Correlation id: {explanation.CorrelationId}",
            $"Rollback classification: {explanation.RollbackClassification}",
            .. explanation.Sections.Select(section => $"Section `{section.Section}`: {section.Status} — {section.Detail}")
        ];
        findings.AddRange(explanation.BlockingReasons.Select(reason => $"Blocking: {reason}"));

        return new OperationResponse(
            Status: explanation.Readiness,
            Summary: explanation.Summary,
            Findings: findings,
            Actions:
            [
                "Explanation is read-only; it interprets supplied release-package evidence and never computes compatibility.",
                explanation.Mode == ProposalMode
                    ? "Proposal mode emits a governed, approval-required handoff suggestion only; it never submits, applies, or rolls back."
                    : "Explanation mode performs no mutation and creates no server operation.",
                .. explanation.RequiredApprovals.Select(approval => $"Required approval: {approval}"),
                .. explanation.SuggestedActions.Select(action =>
                    $"Suggested action `{action.Id}`: {action.Title} (requiresApproval={action.RequiresApproval}, mutatesState={action.MutatesState}).")
            ],
            ValidationChecks:
            [
                "explanation-read-only-no-compute",
                "release-package-evidence-interpreted-not-scraped",
                "free-text-redacted",
                "rollback-classification-derived"
            ],
            Risks:
            [
                .. explanation.ResidualRisks,
                "Readiness reflects the supplied release-package evidence at explanation time and can drift before promotion.",
                explanation.RollbackClassification == RollbackClass.Irreversible
                    ? "Rollback is classified irreversible; only a forward fix can recover this release."
                    : "Acting on this explanation still requires the governed approval/submit path."
            ],
            ConsoleBridge: projection);
    }

    private OperationResponse BuildUnknown(string mode, string correlationId, string? parseError, string timestamp)
    {
        EvidenceRef missing = new(
            Type: BridgeStatus.EvidenceMissing,
            Source: "console",
            RawRef: null,
            Url: null,
            Summary: parseError ?? "Release-package document was empty or could not be parsed.",
            CapturedAt: timestamp,
            Sensitivity: EvidenceSensitivity.Internal);

        ReleaseExplanationSection unknownSection = new(
            Section: "release-package",
            Status: ReleaseReadiness.Unknown,
            Detail: "Release-package evidence could not be parsed; nothing was interpreted.",
            Findings: [parseError ?? "Document was empty or malformed."],
            Evidence: [missing]);

        ReleaseExplanation explanation = new(
            ExplanationId: $"release-explanation:{ShortHash($"unknown:{correlationId}:{timestamp}")}",
            OperationId: null,
            CorrelationId: correlationId,
            Mode: mode,
            ReleaseId: "unknown-release",
            Service: "unknown",
            TargetEnvironments: [],
            DesiredRevision: "unknown",
            Readiness: ReleaseReadiness.Unknown,
            Summary: "Release package could not be explained: the supplied evidence document was empty or malformed.",
            Sections: [unknownSection],
            PromotionGates: [],
            RequiredApprovals: [],
            ResidualRisks: ["Readiness is unknown because no release-package evidence could be interpreted."],
            RollbackClassification: RollbackClass.Unknown,
            Evidence: [missing],
            SuggestedActions: [ReviewEvidenceSuggestion(operationId: null)],
            WorkflowLinks: [SelfLink(operationId: null)],
            BlockingReasons: ["Release-package evidence is missing or unparseable; supply a valid document to explain."],
            CreatedAt: timestamp);

        return BuildResponse(explanation);
    }

    // -- Sections -------------------------------------------------------------

    private ReleaseExplanationSection BuildCompatibilitySection(
        JsonElement root,
        string timestamp,
        List<EvidenceRef> evidence)
    {
        if (!TryGetObject(root, out JsonElement report, "compatibility", "compatibilityReport"))
        {
            return EvidenceMissingSection(
                "compatibility",
                "No compatibility report was supplied; server compatibility is owned by honua-server (#57) and not computed here.");
        }

        string reportStatus = (TryGetString(report, "status", "result") ?? "unknown").ToLowerInvariant();
        int breakingCount = ReadInt(report, "breakingChanges", "breaking");
        int warningCount = ReadInt(report, "warnings", "warning");
        List<string> findings = ReadStringList(report, "findings", "notes");

        string status = reportStatus switch
        {
            "compatible" or "ok" or "pass" or "passed" when breakingCount == 0 && warningCount == 0 => ReleaseReadiness.Ready,
            "incompatible" or "fail" or "failed" or "blocked" => ReleaseReadiness.Blocked,
            "unknown" or "" => ReleaseReadiness.Unknown,
            _ => breakingCount > 0 ? ReleaseReadiness.Blocked : warningCount > 0 ? ReleaseReadiness.Warning : ReleaseReadiness.Ready
        };
        if (status == ReleaseReadiness.Ready && warningCount > 0)
        {
            status = ReleaseReadiness.Warning;
        }

        if (findings.Count == 0)
        {
            findings.Add($"Server compatibility report status `{reportStatus}` with {breakingCount} breaking and {warningCount} warning change(s).");
        }

        EvidenceRef reportEvidence = EvidenceFromReport("compatibility-report", report, timestamp);
        evidence.Add(reportEvidence);

        string detail = status switch
        {
            ReleaseReadiness.Blocked => $"Compatibility report flags {breakingCount} breaking change(s); release is blocked.",
            ReleaseReadiness.Warning => $"Compatibility report is clean but carries {warningCount} advisory warning(s).",
            ReleaseReadiness.Unknown => "Compatibility report status is unknown.",
            _ => "Compatibility report is clean."
        };
        return new ReleaseExplanationSection("compatibility", status, detail, findings, [reportEvidence]);
    }

    private ReleaseExplanationSection BuildScriptCoverageSection(
        JsonElement root,
        string timestamp,
        List<EvidenceRef> evidence)
    {
        if (!TryGetObject(root, out JsonElement coverage, "scriptCoverage", "scripts", "coverage"))
        {
            return EvidenceMissingSection(
                "script-coverage",
                "No script-coverage evidence was supplied.");
        }

        int covered = ReadInt(coverage, "covered", "coveredScripts");
        int total = ReadInt(coverage, "total", "totalScripts", "required");
        List<string> uncovered = ReadStringList(coverage, "uncovered", "missing", "gaps");
        List<string> findings = ReadStringList(coverage, "findings", "notes");

        string status;
        string detail;
        if (total <= 0)
        {
            status = ReleaseReadiness.Unknown;
            detail = "Script-coverage totals were not supplied; coverage is unknown.";
        }
        else if (uncovered.Count > 0 || covered < total)
        {
            status = ReleaseReadiness.Warning;
            detail = $"{covered}/{total} migration/release scripts covered; {Math.Max(total - covered, uncovered.Count)} gap(s) remain.";
        }
        else
        {
            status = ReleaseReadiness.Ready;
            detail = $"All {total} migration/release scripts are covered.";
        }

        if (findings.Count == 0)
        {
            findings.Add(detail);
            findings.AddRange(uncovered.Select(item => $"Uncovered: {item}"));
        }

        EvidenceRef coverageEvidence = EvidenceFromReport("script-coverage", coverage, timestamp);
        evidence.Add(coverageEvidence);
        return new ReleaseExplanationSection("script-coverage", status, detail, findings, [coverageEvidence]);
    }

    private ReleaseExplanationSection BuildPrPreviewSection(
        JsonElement root,
        string timestamp,
        List<EvidenceRef> evidence)
    {
        if (!TryGetObject(root, out JsonElement pr, "prPreview", "pullRequest", "pr"))
        {
            return EvidenceMissingSection(
                "pr-preview",
                "No PR preview was supplied; durable PR/CI evidence is owned by honua-server (#59/#58) and not scraped from GitHub.");
        }

        string? prUrl = TryGetString(pr, "url", "href");
        string? prRef = TryGetString(pr, "number", "id", "ref");
        string ciStatus = (TryGetString(pr, "ciStatus", "ci", "checks") ?? "unknown").ToLowerInvariant();
        List<string> findings = ReadStringList(pr, "findings", "summary", "notes");

        string status = ciStatus switch
        {
            "green" or "success" or "passed" or "pass" => ReleaseReadiness.Ready,
            "red" or "failure" or "failed" or "fail" => ReleaseReadiness.Blocked,
            "pending" or "running" or "queued" => ReleaseReadiness.Warning,
            _ => ReleaseReadiness.Unknown
        };

        if (findings.Count == 0)
        {
            findings.Add($"PR preview {prRef ?? "(unreferenced)"} with CI status `{ciStatus}`.");
        }

        // The PR url/ref is a Git/CI artifact reference, not a value to surface verbatim as a
        // link the bridge owns; carry it as a redacted external evidence ref so Console can
        // resolve it through its own deep-link policy.
        EvidenceRef prEvidence = new(
            Type: "pr-preview",
            Source: "git",
            RawRef: prRef is null ? null : Redaction.Scrub(prRef),
            Url: prUrl is null ? null : Redaction.Scrub(prUrl),
            Summary: Redaction.Scrub($"PR preview {prRef ?? "(unreferenced)"} :: ci={ciStatus}"),
            CapturedAt: timestamp,
            Sensitivity: EvidenceSensitivity.Internal);
        evidence.Add(prEvidence);

        string detail = status switch
        {
            ReleaseReadiness.Blocked => "PR preview reports failing CI; release is blocked.",
            ReleaseReadiness.Warning => "PR preview CI is still pending.",
            ReleaseReadiness.Unknown => "PR preview CI status is unknown.",
            _ => "PR preview CI is green."
        };
        return new ReleaseExplanationSection("pr-preview", status, detail, findings, [prEvidence]);
    }

    private ReleaseExplanationSection BuildPromotionGatesSection(
        JsonElement root,
        string timestamp,
        List<EvidenceRef> evidence,
        out List<PromotionGate> gates)
    {
        gates = [];
        if (!TryGetArray(root, out JsonElement gateArray, "promotionGates", "gates"))
        {
            return EvidenceMissingSection(
                "promotion-gates",
                "No promotion gates were supplied.");
        }

        foreach (JsonElement gateElement in gateArray.EnumerateArray())
        {
            if (gateElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string id = DeploymentInputs.SanitizeFreeText(Redaction.Scrub(TryGetString(gateElement, "id", "name")), "gate");
            string label = DeploymentInputs.SanitizeFreeText(Redaction.Scrub(TryGetString(gateElement, "label", "title", "name")), id);
            bool satisfied = ReadBool(gateElement, "satisfied", "passed", "met");
            bool blocking = ReadBool(gateElement, "blocking", "required", "hard");
            string detail = DeploymentInputs.SanitizeFreeText(
                Redaction.Scrub(TryGetString(gateElement, "detail", "reason", "summary")),
                satisfied ? "Gate satisfied." : "Gate not satisfied.");
            gates.Add(new PromotionGate(id, label, satisfied, blocking, detail));
        }

        EvidenceRef gateEvidence = new(
            Type: "promotion-gates",
            Source: "honua-server",
            RawRef: $"promotion-gates:{gates.Count}",
            Url: null,
            Summary: $"{gates.Count} promotion gate(s) supplied with the release package.",
            CapturedAt: timestamp,
            Sensitivity: EvidenceSensitivity.Internal);
        evidence.Add(gateEvidence);

        bool hardBlocked = gates.Any(gate => gate.Blocking && !gate.Satisfied);
        bool softUnmet = gates.Any(gate => !gate.Blocking && !gate.Satisfied);
        string status = hardBlocked ? ReleaseReadiness.Blocked : softUnmet ? ReleaseReadiness.Warning : ReleaseReadiness.Ready;
        string sectionDetail = hardBlocked
            ? $"{gates.Count(gate => gate.Blocking && !gate.Satisfied)} blocking promotion gate(s) unmet."
            : softUnmet
                ? $"{gates.Count(gate => !gate.Satisfied)} advisory promotion gate(s) unmet."
                : $"All {gates.Count} promotion gate(s) satisfied.";

        List<string> findings = gates
            .Select(gate => $"{(gate.Satisfied ? "PASS" : gate.Blocking ? "BLOCK" : "WARN")} {gate.Label}: {gate.Detail}")
            .ToList();

        return new ReleaseExplanationSection("promotion-gates", status, sectionDetail, findings, [gateEvidence]);
    }

    private ReleaseExplanationSection BuildRollbackSection(
        JsonElement root,
        string timestamp,
        List<EvidenceRef> evidence,
        out string rollbackClass)
    {
        if (!TryGetObject(root, out JsonElement rollback, "rollbackPlan", "rollback"))
        {
            rollbackClass = RollbackClass.Unknown;
            return EvidenceMissingSection(
                "rollback-plan",
                "No rollback plan was supplied; rollback classification is unknown.");
        }

        string rawClass = (TryGetString(rollback, "classification", "class", "kind") ?? "unknown").ToLowerInvariant();
        bool required = ReadBool(rollback, "required", "rollbackRequired");
        string? priorRevision = TryGetString(rollback, "priorRevision", "previousRevision", "to");
        List<string> findings = ReadStringList(rollback, "findings", "steps", "notes");

        rollbackClass = rawClass switch
        {
            "automatic" or "auto" => RollbackClass.Automatic,
            "manual" => RollbackClass.Manual,
            "irreversible" or "forward-only" or "forward-fix" => RollbackClass.Irreversible,
            "not-required" or "none" => RollbackClass.NotRequired,
            _ => required ? RollbackClass.Manual : RollbackClass.Unknown
        };

        string status = rollbackClass switch
        {
            RollbackClass.Irreversible => ReleaseReadiness.Warning,
            RollbackClass.Unknown => ReleaseReadiness.Unknown,
            _ when required => ReleaseReadiness.RollbackRequired,
            _ => ReleaseReadiness.Ready
        };

        if (findings.Count == 0)
        {
            findings.Add($"Rollback classification `{rollbackClass}`{(priorRevision is null ? string.Empty : $"; prior revision {Redaction.Scrub(priorRevision)}")}.");
        }

        EvidenceRef rollbackEvidence = new(
            Type: "rollback-plan",
            Source: "honua-server",
            RawRef: priorRevision is null ? $"rollback:{rollbackClass}" : Redaction.Scrub($"rollback:{rollbackClass}:{priorRevision}"),
            Url: null,
            Summary: Redaction.Scrub($"Rollback plan classification {rollbackClass}, required={required}."),
            CapturedAt: timestamp,
            Sensitivity: EvidenceSensitivity.Internal);
        evidence.Add(rollbackEvidence);

        string detail = rollbackClass switch
        {
            RollbackClass.Irreversible => "Rollback is irreversible; only a forward fix can recover.",
            RollbackClass.NotRequired => "No rollback is required for this release.",
            RollbackClass.Unknown => "Rollback classification is unknown.",
            _ when required => $"A {rollbackClass} rollback is required to recover this release.",
            _ => $"A {rollbackClass} rollback path is available if needed."
        };
        return new ReleaseExplanationSection("rollback-plan", status, detail, findings, [rollbackEvidence]);
    }

    // -- Readiness / risk derivation -----------------------------------------

    private static string DeriveReadiness(
        IReadOnlyList<ReleaseExplanationSection> sections,
        IReadOnlyList<PromotionGate> gates,
        string rollbackClass)
    {
        if (gates.Any(gate => gate.Blocking && !gate.Satisfied)
            || sections.Any(section => section.Status == ReleaseReadiness.Blocked))
        {
            // A blocked release whose only safe path forward is reverting reads as
            // rollback-required so Console steers the operator to the recovery flow.
            if (rollbackClass is RollbackClass.Automatic or RollbackClass.Manual
                && sections.Any(section => section.Section == "rollback-plan" && section.Status == ReleaseReadiness.RollbackRequired))
            {
                return ReleaseReadiness.RollbackRequired;
            }

            return ReleaseReadiness.Blocked;
        }

        if (sections.Any(section => section.Status == ReleaseReadiness.RollbackRequired))
        {
            return ReleaseReadiness.RollbackRequired;
        }

        // A "ready" section is a positive signal; "warning" is a soft signal; "unknown" and
        // "evidence-missing" carry no signal. With no positive/warning signal at all, the
        // release is unknown rather than optimistically ready.
        bool anyReady = sections.Any(section => section.Status == ReleaseReadiness.Ready);
        bool anyWarning = sections.Any(section => section.Status == ReleaseReadiness.Warning)
            || gates.Any(gate => !gate.Satisfied);

        if (!anyReady && !anyWarning)
        {
            return ReleaseReadiness.Unknown;
        }

        if (anyWarning || sections.Any(section => section.Status == ReleaseReadiness.Unknown))
        {
            return ReleaseReadiness.Warning;
        }

        return ReleaseReadiness.Ready;
    }

    private static List<string> CollectBlockingReasons(
        IReadOnlyList<ReleaseExplanationSection> sections,
        IReadOnlyList<PromotionGate> gates,
        string rollbackClass,
        string readiness)
    {
        List<string> reasons = [];
        foreach (ReleaseExplanationSection section in sections.Where(section => section.Status == ReleaseReadiness.Blocked))
        {
            reasons.Add($"{section.Section}: {section.Detail}");
        }

        foreach (PromotionGate gate in gates.Where(gate => gate.Blocking && !gate.Satisfied))
        {
            reasons.Add($"promotion-gate `{gate.Id}`: {gate.Detail}");
        }

        if (readiness == ReleaseReadiness.RollbackRequired)
        {
            reasons.Add($"Release requires a {rollbackClass} rollback before it can be considered recovered.");
        }

        return reasons;
    }

    private string BuildSummary(
        string service,
        IReadOnlyList<string> environments,
        string desiredRevision,
        string readiness,
        string rollbackClass,
        IReadOnlyList<PromotionGate> gates)
    {
        string envs = environments.Count == 0 ? "the requested environment(s)" : string.Join(", ", environments);
        int unmetGates = gates.Count(gate => !gate.Satisfied);
        string gateClause = gates.Count == 0
            ? "no promotion gates were supplied"
            : unmetGates == 0
                ? $"all {gates.Count} promotion gate(s) are satisfied"
                : $"{unmetGates} of {gates.Count} promotion gate(s) are unmet";

        string verdict = readiness switch
        {
            ReleaseReadiness.Ready => $"Release `{desiredRevision}` for `{service}` is ready to promote to {envs}: {gateClause}.",
            ReleaseReadiness.Warning => $"Release `{desiredRevision}` for `{service}` can promote to {envs} with advisory warnings: {gateClause}.",
            ReleaseReadiness.Blocked => $"Release `{desiredRevision}` for `{service}` is blocked from promoting to {envs}: {gateClause}.",
            ReleaseReadiness.RollbackRequired => $"Release `{desiredRevision}` for `{service}` requires a {rollbackClass} rollback to recover {envs}; {gateClause}.",
            _ => $"Release `{desiredRevision}` for `{service}` readiness is unknown; {gateClause}."
        };
        return $"{verdict} Rollback classification: {rollbackClass}.";
    }

    private List<SuggestedAction> BuildSuggestedActions(
        string mode,
        string readiness,
        string rollbackClass,
        string? operationId)
    {
        List<SuggestedAction> actions = [ReviewEvidenceSuggestion(operationId)];

        if (readiness == ReleaseReadiness.RollbackRequired
            && rollbackClass is RollbackClass.Automatic or RollbackClass.Manual)
        {
            actions.Add(new SuggestedAction(
                Id: "prepare-rollback",
                Title: "Prepare governed rollback",
                Description: $"Route a {rollbackClass} rollback through the governed deploy-control path. Requires explicit approval; the bridge never rolls back.",
                RequiresApproval: true,
                MutatesState: true,
                TargetOperationId: operationId,
                WorkflowLink: SelfLink(operationId),
                Kind: "governed-rollback"));
            return actions;
        }

        // Proposal mode offers a governed PR-creation handoff only when the release is not
        // blocked; it is approval-gated and never executed here.
        if (mode == ProposalMode && readiness is ReleaseReadiness.Ready or ReleaseReadiness.Warning)
        {
            actions.Add(new SuggestedAction(
                Id: "create-release-pr",
                Title: "Create release PR (handoff)",
                Description: "Hand off PR creation through the governed create-proposal path. Requires explicit approval; the bridge never creates the PR or submits.",
                RequiresApproval: true,
                MutatesState: true,
                TargetOperationId: operationId,
                WorkflowLink: SelfLink(operationId),
                Kind: "governed-proposal"));
        }

        return actions;
    }

    // -- Evidence / link helpers ---------------------------------------------

    private static ReleaseExplanationSection EvidenceMissingSection(string section, string detail)
        => new(section, BridgeStatus.EvidenceMissing, detail, [detail], []);

    private static EvidenceRef EvidenceFromReport(string type, JsonElement report, string timestamp)
    {
        string raw = Redaction.Scrub(report.GetRawText());
        string preview = raw.Length <= 240 ? raw : raw[..240];
        return new EvidenceRef(
            Type: type,
            Source: "honua-server",
            RawRef: $"{type}",
            Url: null,
            Summary: preview,
            CapturedAt: timestamp,
            Sensitivity: EvidenceSensitivity.Internal);
    }

    private EvidenceRef ServerOperationEvidence(string operationId, string timestamp)
    {
        string? href = ComposeUrl(configuration.HonuaApiBaseUri, $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}");
        return new EvidenceRef(
            Type: "deploy-operation",
            Source: "honua-server",
            RawRef: $"deploy-operation:{operationId}",
            Url: href,
            Summary: "honua-server deploy-control operation linked to this release explanation.",
            CapturedAt: timestamp,
            Sensitivity: EvidenceSensitivity.Internal);
    }

    private WorkflowLink SelfLink(string? operationId)
    {
        string? href = operationId is null ? null : ComposeUrl(configuration.ConsoleBaseUri, $"operations/{Uri.EscapeDataString(operationId)}");
        return new WorkflowLink("self", "Open in Console", href, href is not null);
    }

    private WorkflowLink ServerOperationLink(string operationId)
    {
        string? href = ComposeUrl(configuration.HonuaApiBaseUri, $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}");
        return new WorkflowLink("server-operation", "Deploy-control operation", href, href is not null);
    }

    private static SuggestedAction ReviewEvidenceSuggestion(string? operationId)
        => new(
            Id: "review-explanation-evidence",
            Title: "Review release explanation evidence",
            Description: "Review the compatibility, script-coverage, PR-preview, promotion-gate, and rollback evidence before promoting.",
            RequiresApproval: false,
            MutatesState: false,
            TargetOperationId: operationId,
            WorkflowLink: null,
            Kind: "advisory");

    // -- Input parsing --------------------------------------------------------

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

            // Clone so the element outlives the disposed JsonDocument.
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Release-package document is not valid JSON: {Redaction.Scrub(exception.Message)}";
            return false;
        }
    }

    private static string NormalizeMode(string? mode)
        => DeploymentInputs.Normalize(mode, ExplanationMode).ToLowerInvariant() switch
        {
            ProposalMode => ProposalMode,
            _ => ExplanationMode
        };

    private static string NormalizeCorrelationId(string? value)
    {
        string normalized = DeploymentInputs.Normalize(value, string.Empty);
        if (normalized.Length == 0)
        {
            return $"console:{ShortHash(Timestamp())}";
        }

        // Bound length and keep it link/header-safe; correlation ids thread through Console,
        // server release ids, PRs, CI, and GitOps operations, so they must stay opaque tokens.
        string scrubbed = Redaction.Scrub(normalized);
        string compact = new string(scrubbed.Where(character => !char.IsControl(character) && !char.IsWhiteSpace(character)).ToArray());
        if (compact.Length == 0)
        {
            return $"console:{ShortHash(Timestamp())}";
        }

        return compact.Length <= 200 ? compact : compact[..200];
    }

    private static string? NormalizeOperationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string operationId = value.Trim();
        if (operationId.Length is < 1 or > 200
            || operationId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return null;
        }

        return operationId;
    }

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
            return DeploymentInputs.SanitizeFreeText(value, "unknown");
        }
    }

    private static string[] ReadStringArray(JsonElement root, params string[] names)
        => ReadStringList(root, names).ToArray();

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
                            // Scrub before sanitize so a secret embedded in a finding never
                            // reaches the Console-facing projection verbatim.
                            string sanitized = DeploymentInputs.SanitizeFreeText(Redaction.Scrub(item.GetString()), string.Empty);
                            if (sanitized.Length > 0)
                            {
                                values.Add(sanitized);
                            }
                        }
                    }

                    return values;
                case JsonValueKind.String:
                    string single = DeploymentInputs.SanitizeFreeText(Redaction.Scrub(property.Value.GetString()), string.Empty);
                    if (single.Length > 0)
                    {
                        values.Add(single);
                    }

                    return values;
            }
        }

        return values;
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

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string? ComposeUrl(Uri? baseUri, string relativePath)
    {
        if (baseUri is null)
        {
            return null;
        }

        string cleaned = relativePath.TrimStart('/');
        UriBuilder builder = new(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return new Uri(builder.Uri, cleaned).ToString();
    }
}
