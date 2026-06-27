using System.ComponentModel;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.Deliverable;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.Troubleshooting;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

// Support-ticket triage tools split out of HonuaOperationsToolkit (audit #118).
// Part of the same class; shares the primary constructor (runtime, gateway,
// policy, supportGateway, defaultEdition) and helpers declared in the core file.
internal sealed partial class HonuaOperationsToolkit
{
    [Description("Triage a support ticket with read-only diagnosis, guided-fix commands, or operator-scoped escalation.")]
    public async Task<OperationResponse> TriageSupportTicketAsync(
        string ticketId,
        string severity,
        string environment,
        string symptoms,
        string requestedAction,
        string allowedAccessMode,
        int ttlMinutes,
        bool rollbackExpected,
        string attachedEvidence,
        CancellationToken cancellationToken = default)
    {
        string normalizedTicketId = SanitizePayloadValue(ticketId, "ticket ID");
        SupportSeverity parsedSeverity = SupportSeverityExtensions.Parse(severity);
        string normalizedEnvironment = Normalize(environment, "unknown");
        string normalizedSymptoms = SanitizeFreeText(symptoms, string.Empty);
        string normalizedRequestedAction = SanitizeFreeText(requestedAction, "diagnose");
        string normalizedAccessMode = Normalize(allowedAccessMode, "read-only");
        int effectiveTtl = ttlMinutes is < 1 or > 1440 ? EffectivePolicy.SupportSession.TtlMinutes : ttlMinutes;
        string normalizedEvidence = SanitizeFreeText(attachedEvidence, string.Empty);

        SupportTicket ticket = new(
            TicketId: normalizedTicketId,
            Service: "support-triage",
            Severity: parsedSeverity,
            Environment: normalizedEnvironment,
            Symptoms: normalizedSymptoms,
            RequestedAction: normalizedRequestedAction,
            AllowedAccessMode: normalizedAccessMode,
            TtlMinutes: effectiveTtl,
            RollbackExpected: rollbackExpected,
            AttachedEvidence: normalizedEvidence);

        BackendCallResult backendResult = await gateway.RequestTroubleshootAsync(
            "support-triage",
            normalizedEnvironment,
            normalizedSymptoms,
            normalizedRequestedAction,
            $"ticket:{normalizedTicketId}",
            cancellationToken);

        GuidedFixResult guidedFix = GuidedFixPlanner.Build(
            ticket,
            EffectivePolicy,
            runtime.ExecutionMode,
            runtime.ExecutionTier,
            backendResult);

        List<string> findings =
        [
            $"Ticket: {normalizedTicketId} ({parsedSeverity.ToConfigValue()}).",
            $"Environment: {normalizedEnvironment}.",
            $"Honua API endpoint: {backendResult.Endpoint}",
            $"Backend result: {backendResult.Detail}",
            $"Diagnosis confidence: {guidedFix.Confidence}.",
            $"Guided-fix mode: {guidedFix.Mode.ToConfigValue()}.",
            $"Recommended next action: {guidedFix.RecommendedNextAction}.",
            $"Diagnosis: {guidedFix.DiagnosisSummary}",
            $"Approval mode: {EffectivePolicy.ApprovalMode.ToConfigValue()}.",
            $"Support session access: {EffectivePolicy.SupportSession.Access.ToConfigValue()} ({EffectivePolicy.SupportSession.TtlMinutes}m TTL).",
            $"Customer-visible: {EffectivePolicy.SupportSession.CustomerVisible}."
        ];

        if (guidedFix.MatchedFault is not null)
        {
            findings.Add($"Matched fault: {guidedFix.MatchedFault.ScenarioName} ({guidedFix.MatchedFault.FaultCategory}).");
            findings.Add($"Match score: {guidedFix.MatchedFault.MatchScore:F0}% ({guidedFix.MatchedFault.MatchedIndicators.Count} indicators).");
            findings.Add($"Remediation scope: {guidedFix.MatchedFault.RemediationScope.ToConfigValue()}.");
            findings.Add($"Rollback path: {guidedFix.MatchedFault.RollbackPath}.");
        }

        if (guidedFix.MissingEvidence.Count > 0)
        {
            findings.AddRange(guidedFix.MissingEvidence.Select(missing => $"Missing: {missing}"));
        }

        List<string> actions =
        [
            $"Operate in `{guidedFix.Mode.ToConfigValue()}` posture for this ticket.",
            ..guidedFix.GuidedCommands
        ];

        if (guidedFix.Escalation is not null)
        {
            actions.Add($"Escalation requires approval: {string.Join(", ", guidedFix.Escalation.RequiredApprovalContext)}.");
            actions.Add($"Escalation access scope: {guidedFix.Escalation.AccessScope} with TTL {guidedFix.Escalation.TtlMinutes}m.");
            actions.Add($"Rollback intent: {guidedFix.Escalation.RollbackIntent}.");
        }

        actions.AddRange(BuildOperatorPolicyActions());

        List<string> validationChecks = [.. guidedFix.ValidationSteps];
        validationChecks.AddRange(BuildOperatorPolicyValidationChecks());

        List<string> risks =
        [
            "Diagnosis accuracy depends on telemetry completeness and symptom quality.",
            "Guided commands should be validated by the customer before execution."
        ];
        risks.AddRange(BuildOperatorPolicyRisks());

        if (guidedFix.Mode == GuidedFixMode.OperatorScoped)
        {
            risks.Add("Operator-scoped access must stay within ticket scope and TTL to avoid silent privilege expansion.");
        }

        return new OperationResponse(
            Status: backendResult.IsSuccess
                ? guidedFix.Mode.ToConfigValue()
                : "backend-error",
            Summary: $"Support triage for ticket {normalizedTicketId} ({parsedSeverity.ToConfigValue()}) in `{normalizedEnvironment}`.",
            Findings: findings,
            Actions: actions,
            ValidationChecks: validationChecks,
            Risks: risks,
            Evidence: BuildGuidedFixEvidence(ticket, guidedFix, backendResult));
    }

    [Description("Build a Console-facing view of a support ticket's L2/L3 trust state: the live delegated-session (access mode disabled/read-only/operator-scoped, effective TTL, absolute expiry, customer-visible flag, active flag), the DiagnosisScorecard (pass/fail, composite score, per-criterion checklist, failure modes), the escalation rationale (which signal/trigger caused the operator-scoped hand-off, or not-escalated), and audit-journal references. Read-only projection: runs the same diagnosis as triage but never opens a session, posts a diagnosis, or escalates.")]
    public async Task<OperationResponse> BuildSupportTicketConsoleViewAsync(
        string ticketId,
        string severity,
        string environment,
        string symptoms,
        string requestedAction,
        string allowedAccessMode,
        int ttlMinutes,
        bool rollbackExpected,
        string attachedEvidence,
        CancellationToken cancellationToken = default)
    {
        string normalizedTicketId = SanitizePayloadValue(ticketId, "ticket ID");
        SupportSeverity parsedSeverity = SupportSeverityExtensions.Parse(severity);
        string normalizedEnvironment = Normalize(environment, "unknown");
        string normalizedSymptoms = SanitizeFreeText(symptoms, string.Empty);
        string normalizedRequestedAction = SanitizeFreeText(requestedAction, "diagnose");
        string normalizedAccessMode = Normalize(allowedAccessMode, "read-only");
        int effectiveTtl = ttlMinutes is < 1 or > 1440 ? EffectivePolicy.SupportSession.TtlMinutes : ttlMinutes;
        string normalizedEvidence = SanitizeFreeText(attachedEvidence, string.Empty);

        SupportTicket ticket = new(
            TicketId: normalizedTicketId,
            Service: "support-triage",
            Severity: parsedSeverity,
            Environment: normalizedEnvironment,
            Symptoms: normalizedSymptoms,
            RequestedAction: normalizedRequestedAction,
            AllowedAccessMode: normalizedAccessMode,
            TtlMinutes: effectiveTtl,
            RollbackExpected: rollbackExpected,
            AttachedEvidence: normalizedEvidence);

        BackendCallResult backendResult = await gateway.RequestTroubleshootAsync(
            "support-triage",
            normalizedEnvironment,
            normalizedSymptoms,
            normalizedRequestedAction,
            $"ticket:{normalizedTicketId}",
            cancellationToken);

        GuidedFixResult guidedFix = GuidedFixPlanner.Build(
            ticket,
            EffectivePolicy,
            runtime.ExecutionMode,
            runtime.ExecutionTier,
            backendResult);

        OperationEvidence operationEvidence = BuildGuidedFixEvidence(ticket, guidedFix, backendResult);
        DiagnosisScorecard scorecard = BuildSupportDiagnosisScorecard(ticket, guidedFix, backendResult);

        string timestamp = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset establishedAt = DateTimeOffset.UtcNow;
        string auditScope = operationEvidence.Scope;

        List<EvidenceRef> scorecardEvidence =
        [
            new EvidenceRef(
                Type: "backend-troubleshoot",
                Source: "honua-server",
                RawRef: backendResult.Endpoint,
                Url: backendResult.Endpoint,
                Summary: $"{backendResult.Detail} :: {backendResult.PayloadPreview}",
                CapturedAt: timestamp,
                Sensitivity: EvidenceSensitivity.Internal),
            SupportSessionBridge.AuditReference(auditScope, EffectivePolicy.AuditHookTarget, timestamp)
        ];

        DelegatedSessionState session = SupportSessionBridge.BuildSessionState(
            ticket,
            guidedFix,
            EffectivePolicy,
            operationEvidence.SupportSessionTtlMinutes,
            establishedAt);
        DiagnosisScorecardBridge scorecardBridge = SupportSessionBridge.BuildScorecard(
            scorecard,
            guidedFix.Confidence,
            scorecardEvidence);
        EscalationRationale escalationRationale = SupportSessionBridge.BuildEscalationRationale(guidedFix);

        SupportTicketConsoleView view = new(
            TicketId: normalizedTicketId,
            Posture: guidedFix.Mode.ToConfigValue(),
            DiagnosisSummary: guidedFix.DiagnosisSummary,
            Session: session,
            Scorecard: scorecardBridge,
            Escalation: escalationRationale,
            AuditReferences: [SupportSessionBridge.AuditReference(auditScope, EffectivePolicy.AuditHookTarget, timestamp)],
            CreatedAt: timestamp);

        ConsoleBridgeProjection projection = new("support-ticket-view", SupportTicket: view);

        List<string> findings =
        [
            $"Ticket: {normalizedTicketId} ({parsedSeverity.ToConfigValue()}) in `{normalizedEnvironment}`.",
            $"Posture: {view.Posture}.",
            $"Session: access={session.AccessMode}, active={session.Active}, ttl={session.TtlMinutes}m, expires={session.ExpiresAt ?? "n/a"}, customer-visible={session.CustomerVisible}.",
            $"Scorecard: {scorecardBridge.OverallResult} (composite {scorecardBridge.CompositeScore}, confidence {scorecardBridge.Confidence}).",
            $"Escalated: {escalationRationale.Escalated} (trigger {escalationRationale.Trigger}).",
            $"Why escalated: {escalationRationale.Signal}",
            $"Audit scope: {auditScope}."
        ];
        if (scorecardBridge.FailureModes.Count > 0)
        {
            findings.Add($"Scorecard failure modes: {string.Join(", ", scorecardBridge.FailureModes)}.");
        }

        return new OperationResponse(
            Status: scorecardBridge.OverallResult,
            Summary: $"Console view for ticket {normalizedTicketId}: {view.Posture} posture, scorecard {scorecardBridge.OverallResult}, escalated={escalationRationale.Escalated}.",
            Findings: findings,
            Actions:
            [
                "Read-only projection of trust state; no session is opened, no diagnosis is posted, and no escalation occurs here.",
                "Route any session approval through the governed support-session path with explicit operator sign-off.",
                $"Required approval context: {string.Join(", ", escalationRationale.RequiredApprovalContext.DefaultIfEmpty("none"))}."
            ],
            ValidationChecks:
            [
                "session-state-derived-from-policy",
                "scorecard-pass-fail-stable",
                "escalation-rationale-trigger-coded"
            ],
            Risks:
            [
                "Session TTL/expiry reflect policy at read time; an approved live session can expire before action.",
                "Scorecard is a heuristic over telemetry completeness and does not replace operator judgment."
            ],
            ConsoleBridge: projection);
    }

    private OperationEvidence BuildGuidedFixEvidence(
        SupportTicket ticket,
        GuidedFixResult guidedFix,
        BackendCallResult backendResult)
    {
        int effectiveSupportSessionTtl = guidedFix.Escalation?.TtlMinutes ??
            Math.Min(ticket.TtlMinutes, EffectivePolicy.SupportSession.TtlMinutes);
        List<string> requiredChecks =
        [
            "ticket-context",
            "diagnosis-evidence",
            "access-mode-record"
        ];
        requiredChecks.AddRange(BuildOperatorPolicyValidationChecks());

        if (guidedFix.Mode == GuidedFixMode.OperatorScoped && guidedFix.Escalation is not null)
        {
            requiredChecks.AddRange(guidedFix.Escalation.RequiredApprovalContext);
        }

        if (ticket.RollbackExpected)
        {
            requiredChecks.Add("rollback-readiness");
        }

        return new OperationEvidence(
            Scope: $"support-triage:{ticket.Service}:{ticket.TicketId}",
            RequestedAction: ticket.RequestedAction,
            EffectiveAction: guidedFix.Mode.ToConfigValue(),
            DryRun: guidedFix.Mode != GuidedFixMode.OperatorScoped,
            ExecutionMode: runtime.ExecutionMode.ToString().ToLowerInvariant(),
            ExecutionTier: runtime.ExecutionTier.ToConfigValue(),
            TargetEnvironments: [ticket.Environment],
            CurrentRevision: null,
            DesiredRevision: null,
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            DeploymentTargets: runtime.TerraformDeploymentTargets.ToArray(),
            PolicyGate: guidedFix.Mode.ToConfigValue(),
            ApprovalMode: EffectivePolicy.ApprovalMode.ToConfigValue(),
            AuditHookTarget: EffectivePolicy.AuditHookTarget,
            SupportSessionAccess: EffectivePolicy.SupportSession.Access.ToConfigValue(),
            SupportSessionTtlMinutes: effectiveSupportSessionTtl,
            SupportSessionCustomerVisible: EffectivePolicy.SupportSession.CustomerVisible,
            BreakGlassPostActionReviewRequired: EffectivePolicy.BreakGlassPostActionReviewRequired,
            RequiredChecks: requiredChecks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DiffSummary: null,
            GateStatus: guidedFix.RecommendedNextAction,
            BackendEndpoint: backendResult.Endpoint,
            BackendDetail: backendResult.Detail);
    }

    private static DiagnosisScorecard BuildSupportDiagnosisScorecard(
        SupportTicket ticket,
        GuidedFixResult guidedFix,
        BackendCallResult backendResult)
    {
        bool diagnosisMatchedCatalog = guidedFix.MatchedFault is not null;
        bool remediationSafe = guidedFix.Mode != GuidedFixMode.OperatorScoped || guidedFix.Escalation is not null;
        bool rollbackGuidancePresent = !ticket.RollbackExpected ||
            !string.IsNullOrWhiteSpace(guidedFix.Escalation?.RollbackIntent) ||
            !string.IsNullOrWhiteSpace(guidedFix.MatchedFault?.RollbackPath) ||
            guidedFix.ValidationSteps.Any(step => step.Contains("rollback", StringComparison.OrdinalIgnoreCase));
        double evidenceQuality = guidedFix.MatchedFault is not null
            ? guidedFix.MatchedFault.MatchScore
            : Math.Max(10, 50 - (guidedFix.MissingEvidence.Count * 10));

        List<string> failureModes = [];
        if (!backendResult.IsSuccess)
        {
            failureModes.Add("backend-troubleshoot-unavailable");
        }

        if (!diagnosisMatchedCatalog)
        {
            failureModes.Add("unmatched-fault-catalog");
        }

        if (!remediationSafe)
        {
            failureModes.Add("unsafe-remediation");
        }

        if (!rollbackGuidancePresent)
        {
            failureModes.Add("missing-or-incorrect-rollback");
        }

        return new DiagnosisScorecard(
            ScenarioId: guidedFix.MatchedFault?.ScenarioId ?? $"support-ticket:{ticket.TicketId}",
            ScenarioName: guidedFix.MatchedFault?.ScenarioName ?? "Support ticket triage",
            DiagnosisCorrect: backendResult.IsSuccess && diagnosisMatchedCatalog,
            DiagnosisLatency: "single-pass",
            EvidenceQuality: Math.Clamp(evidenceQuality, 0, 100),
            RemediationSafe: remediationSafe,
            PolicyCompliant: true,
            RollbackGuidanceCorrect: rollbackGuidancePresent,
            RecoveryVerified: false,
            ServiceHealthRestored: false,
            FailureModes: failureModes);
    }

    [Description("Pull pending support tickets from honua-support, run diagnosis against the fault catalog, and post results back.")]
    public async Task<OperationResponse> ProcessPendingTicketsAsync(CancellationToken cancellationToken = default)
    {
        if (supportGateway is null)
        {
            return new OperationResponse(
                Status: "support-api-disabled",
                Summary: "honua-support integration is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.",
                Findings: ["SupportGateway is not available. Skipping pending ticket processing."],
                Actions: [],
                ValidationChecks: [],
                Risks: []);
        }

        using BackendJsonResult listJsonResult = await supportGateway.ListPendingTicketsJsonAsync(cancellationToken);
        BackendCallResult listResult = listJsonResult.CallResult;
        if (!listResult.IsSuccess)
        {
            return new OperationResponse(
                Status: "backend-error",
                Summary: $"Failed to list pending tickets from honua-support: {listResult.Detail}",
                Findings: [$"Endpoint: {listResult.Endpoint}", $"Detail: {listResult.Detail}", $"Preview: {listResult.PayloadPreview}"],
                Actions: ["Verify HONUA_DEVOPS_SUPPORT_API_BASE_URL is set to a reachable honua-support instance."],
                ValidationChecks: [],
                Risks: ["Pending tickets remain unprocessed while the support API is unreachable."]);
        }

        if (listJsonResult.Payload is null)
        {
            return new OperationResponse(
                Status: "parse-error",
                Summary: "Failed to parse ticket list response from honua-support.",
                Findings: [$"Raw preview: {listResult.PayloadPreview}"],
                Actions: [],
                ValidationChecks: [],
                Risks: ["Pending tickets remain unprocessed due to unparseable API response."]);
        }

        {
            System.Text.Json.JsonDocument ticketsDoc = listJsonResult.Payload;
            List<System.Text.Json.JsonElement> pendingTickets = [];
            if (ticketsDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement element in ticketsDoc.RootElement.EnumerateArray())
                {
                    string phase = element.TryGetProperty("phase", out System.Text.Json.JsonElement phaseElement)
                        ? phaseElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (phase.Equals("intake", StringComparison.OrdinalIgnoreCase) ||
                        phase.Equals("triaging", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingTickets.Add(element);
                    }
                }
            }

            if (pendingTickets.Count == 0)
            {
                return new OperationResponse(
                    Status: "no-pending-tickets",
                    Summary: "No pending tickets found in honua-support (phase=intake or phase=triaging).",
                    Findings: ["All tickets are already past the intake/triaging phase."],
                    Actions: [],
                    ValidationChecks: [],
                    Risks: []);
            }

            List<string> findings = [];
            List<string> actions = [];
            List<string> validationChecks = [];
            List<string> risks = [];
            int processed = 0;
            int diagnosed = 0;

            foreach (System.Text.Json.JsonElement ticketElement in pendingTickets)
            {
                string ticketId = ticketElement.TryGetProperty("id", out System.Text.Json.JsonElement idEl)
                    ? idEl.GetString() ?? "unknown"
                    : "unknown";
                string severity = ticketElement.TryGetProperty("severity", out System.Text.Json.JsonElement sevEl)
                    ? sevEl.GetString() ?? "medium"
                    : "medium";
                string environment = ticketElement.TryGetProperty("environment", out System.Text.Json.JsonElement envEl)
                    ? envEl.GetString() ?? "unknown"
                    : "unknown";
                string service = ticketElement.TryGetProperty("service", out System.Text.Json.JsonElement serviceEl)
                    ? serviceEl.GetString() ?? "support-triage"
                    : "support-triage";
                string symptoms = ticketElement.TryGetProperty("symptoms", out System.Text.Json.JsonElement symEl)
                    ? symEl.GetString() ?? string.Empty
                    : string.Empty;
                string requestedAction = ticketElement.TryGetProperty("requestedAction", out System.Text.Json.JsonElement raEl)
                    ? raEl.GetString() ?? "diagnose"
                    : "diagnose";
                string allowedAccessMode = ticketElement.TryGetProperty("allowedAccessMode", out System.Text.Json.JsonElement aamEl)
                    ? aamEl.GetString() ?? "read-only"
                    : "read-only";
                int ttlMinutes = ticketElement.TryGetProperty("ttlMinutes", out System.Text.Json.JsonElement ttlEl)
                    ? ttlEl.TryGetInt32(out int ttlVal) ? ttlVal : 60
                    : 60;
                bool rollbackExpected = ticketElement.TryGetProperty("rollbackExpected", out System.Text.Json.JsonElement rbEl)
                    && rbEl.ValueKind == System.Text.Json.JsonValueKind.True;
                string? instanceUrl = ticketElement.TryGetProperty("instanceUrl", out System.Text.Json.JsonElement instanceEl)
                    ? instanceEl.GetString()
                    : null;

                SupportSeverity parsedSeverity = SupportSeverityExtensions.Parse(severity);
                SupportTicket ticket = new(
                    TicketId: ticketId,
                    Service: Normalize(service, "support-triage"),
                    Severity: parsedSeverity,
                    Environment: Normalize(environment, "unknown"),
                    Symptoms: Normalize(symptoms, "not provided"),
                    RequestedAction: Normalize(requestedAction, "diagnose"),
                    AllowedAccessMode: Normalize(allowedAccessMode, "read-only"),
                    TtlMinutes: ttlMinutes is < 1 or > 1440 ? EffectivePolicy.SupportSession.TtlMinutes : ttlMinutes,
                    RollbackExpected: rollbackExpected,
                    AttachedEvidence: string.Empty);

                BackendCallResult? autoBundleResult = null;
                if (!string.IsNullOrWhiteSpace(instanceUrl))
                {
                    // Adopt the support-context-v1 superset (honua-console#170): carry the
                    // structured context the console persisted on the ticket plus what devops
                    // knows from triage (tenant/env, instanceUrl, the forwarded read-only key)
                    // so honua-support's collector can scope the auto-bundle. instanceUrl + the
                    // forwarded key stay populated for backward compatibility.
                    SupportContext autoBundleContext = BuildAutoBundleContext(ticketElement, environment);
                    autoBundleResult = await supportGateway.TriggerAutoBundleAsync(
                        ticketId,
                        instanceUrl,
                        supportGateway.Configuration.SupportAutoBundleApiKey,
                        autoBundleContext,
                        cancellationToken);
                }

                BackendCallResult backendResult = await gateway.RequestTroubleshootAsync(
                    ticket.Service,
                    ticket.Environment,
                    ticket.Symptoms,
                    ticket.RequestedAction,
                    $"ticket:{ticketId}",
                    cancellationToken);

                GuidedFixResult guidedFix = GuidedFixPlanner.Build(
                    ticket,
                    EffectivePolicy,
                    runtime.ExecutionMode,
                    runtime.ExecutionTier,
                    backendResult);

                OperationEvidence evidence = BuildGuidedFixEvidence(ticket, guidedFix, backendResult);
                DiagnosisScorecard scorecard = BuildSupportDiagnosisScorecard(ticket, guidedFix, backendResult);

                // Relay the structured TRUST state (honua-support#23): reuse the already-
                // computed #70 projections — delegated session, scorecard, escalation
                // rationale — so honua-support can persist them and surface them to the
                // console without re-deriving from prose. No new trust is computed here.
                string trustTimestamp = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                DateTimeOffset trustEstablishedAt = DateTimeOffset.UtcNow;
                List<EvidenceRef> trustScorecardEvidence =
                [
                    new EvidenceRef(
                        Type: "backend-troubleshoot",
                        Source: "honua-server",
                        RawRef: backendResult.Endpoint,
                        Url: backendResult.Endpoint,
                        Summary: $"{backendResult.Detail} :: {backendResult.PayloadPreview}",
                        CapturedAt: trustTimestamp,
                        Sensitivity: EvidenceSensitivity.Internal),
                    SupportSessionBridge.AuditReference(evidence.Scope, EffectivePolicy.AuditHookTarget, trustTimestamp)
                ];
                DelegatedSessionState trustSession = SupportSessionBridge.BuildSessionState(
                    ticket,
                    guidedFix,
                    EffectivePolicy,
                    evidence.SupportSessionTtlMinutes,
                    trustEstablishedAt);
                DiagnosisScorecardBridge trustScorecard = SupportSessionBridge.BuildScorecard(
                    scorecard,
                    guidedFix.Confidence,
                    trustScorecardEvidence);
                EscalationRationale trustEscalation = SupportSessionBridge.BuildEscalationRationale(guidedFix);
                SupportTicketTrust trust = new(trustSession, trustScorecard, trustEscalation);

                BackendCallResult postResult = await supportGateway.PostDiagnosisAsync(
                    ticketId,
                    guidedFix,
                    evidence,
                    scorecard,
                    trust,
                    cancellationToken);

                processed++;
                if (postResult.IsSuccess)
                {
                    diagnosed++;
                }

                findings.Add($"Ticket {ticketId} ({parsedSeverity.ToConfigValue()}) in `{environment}`: " +
                             $"diagnosis={guidedFix.Confidence}, mode={guidedFix.Mode.ToConfigValue()}, " +
                             $"post-result={postResult.Detail}, " +
                             $"auto-bundle={autoBundleResult?.Detail ?? "not-requested"}.");
                actions.AddRange(guidedFix.GuidedCommands.Take(2).Select(command => $"[{ticketId}] {command}"));
            }

            validationChecks.Add("Verify all diagnosed tickets transitioned to the correct phase in honua-support.");
            validationChecks.Add("When tickets include instanceUrl, verify honua-support auto-bundle captured real Honua health, metrics, and manifest telemetry.");
            risks.Add("Diagnosis accuracy depends on telemetry completeness and symptom quality.");

            return new OperationResponse(
                Status: diagnosed == processed ? "tickets-processed" : "partial-failure",
                Summary: $"Processed {processed} pending ticket(s) from honua-support; {diagnosed} diagnosis result(s) posted successfully.",
                Findings: findings,
                Actions: actions,
                ValidationChecks: validationChecks,
                Risks: risks);
        }
    }
}
