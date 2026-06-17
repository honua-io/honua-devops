using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Microsoft.Extensions.AI;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

internal static class CapabilityToolset
{
    internal static IList<AITool> Create(
        OperationRuntime runtime,
        BackendGateway gateway,
        OperatorPolicyModel? policy = null,
        SupportGateway? supportGateway = null,
        string? defaultEdition = null)
    {
        HonuaOperationsToolkit toolkit = new(runtime, gateway, policy, supportGateway, defaultEdition);
        ConsoleOperationBridge consoleBridge = new(runtime, gateway, policy);
        ReleasePackageExplainer releaseExplainer = new(gateway.Configuration);

        return
        [
            CreateTool(
                () => toolkit.DescribeEnvironmentAsync(),
                "describe_environment",
                "Discover the connected Honua environment: readiness, edition, manifest scope, deploy targets, and allowed environments. Call first when the operator's request lacks an explicit service, environment, or edition."),
            CreateTool(
                (string toolFilter, bool mutatedOnly, string statusContains, int limit)
                    => toolkit.FindRecentOperationsAsync(toolFilter, mutatedOnly, statusContains, limit),
                "find_recent_operations",
                "Search the audit journal for recent operations across sessions. Returns operationId, timestamp, tool, status, mutated flag, and summary. Use to look up a prior operationId for rollback, recall what ran yesterday, or audit mutating calls. Filters: toolFilter (exact tool name, empty for any), mutatedOnly (true skips reads), statusContains (substring), limit (1-200, default 20)."),
            CreateTool(
                (string service, string environment, string timeframe, string symptoms, string logSample)
                    => toolkit.AnalyzeLogsAsync(service, environment, timeframe, symptoms, logSample),
                "analyze_logs",
                "Analyze logs and return findings, prioritized remediation, and validation checks."),
            CreateTool(
                (string service, string environment, string timeframe, string objective, string metricSnapshot)
                    => toolkit.AnalyzeMetricsAsync(service, environment, timeframe, objective, metricSnapshot),
                "analyze_metrics",
                "Analyze metrics and identify performance bottlenecks with optimization priorities."),
            CreateTool(
                (string service, string environment, string workloadProfile, string bottleneck, string targetSlo)
                    => toolkit.TunePerformanceAsync(service, environment, workloadProfile, bottleneck, targetSlo),
                "tune_performance",
                "Create a performance tuning plan for Honua services."),
            CreateTool(
                (string service, string environment, string incidentSummary, string suspectedComponent, string businessImpact)
                    => toolkit.TroubleshootIncidentAsync(service, environment, incidentSummary, suspectedComponent, businessImpact),
                "troubleshoot_incident",
                "Troubleshoot an incident and provide ordered response actions."),
            CreateTool(
                (string environment, string currentVersion, string targetVersion, string maintenanceWindow, string constraints)
                    => toolkit.PlanServerUpgradeAsync(environment, currentVersion, targetVersion, maintenanceWindow, constraints),
                "plan_server_upgrade",
                "Plan a Honua server upgrade with staged rollout and rollback criteria."),
            CreateTool(
                (string service, string environmentsCsv, string revision, string action, string changeSummary)
                    => toolkit.PlanGitOpsEngineAsync(service, environmentsCsv, revision, action, changeSummary),
                "plan_gitops_engine",
                "Plan the internal honua-gitops engine diff, drift, and state transitions without applying desired state."),
            CreateTool(
                (string releasePackageJson)
                    => toolkit.GenerateMetadataReleaseChangeSetAsync(releasePackageJson),
                "generate_metadata_release_changeset",
                "Generate a PR-ready desired-state change set from a validated metadata release package: deterministic branch name, commit message, PR title/body with evidence links, repo-relative file path -> content map (metadata manifests, environment overlays, optional data-script coverage, validation evidence, rollback policy), rollback commands derived from the rollback classification and known-good revision, and an overall ready/warning/blocked/unknown readiness. Read-only: renders Git artifacts in-process and never writes Git, opens a PR, applies manifests, or creates a server operation; merge/reconcile runs through the governed GitOps/approval path. Blocked when compatibility flags breaking changes."),
            CreateTool(
                (string releasePackageJson)
                    => toolkit.PlanMetadataReleaseGitOpsAsync(releasePackageJson),
                "plan_metadata_release_gitops",
                "Plan a metadata-release-aware honua-gitops run from a validated metadata release package. Fuses the PR-ready change set with the honua-gitops planner so a single read-only output carries BOTH the desired-state change set AND a metadata-release-aware gitops plan: per-environment diff/drift/state transitions tagged in-scope vs not-targeted, plus a metadata-release summary (semantic resources, compatibility verdict, breaking-change count, script coverage, rollback classification, known-good revision, blocking reasons). Default-safe and deterministic: never calls the backend, submits, rolls back, or mutates state; merge/reconcile runs through the governed GitOps/approval path. Returns a blocked plan surfacing blocking reasons when compatibility flags breaking changes, and a graceful unknown response when the document cannot be parsed."),
            CreateTool(
                (string service, string environmentsCsv, string revision, string action, string changeSummary)
                    => toolkit.DeployServiceWithGitOpsAsync(service, environmentsCsv, revision, action, changeSummary),
                "deploy_service_gitops",
                "Generate GitOps deployment actions across environments, and (only when EXECUTION_MODE=execute and the approval gate is satisfied) actuate sync/promote THROUGH the honua-server deploy-control endpoints: validate (preflight+plan), create a durable operation (submitImmediately=false), pause for external approval under pr-first or when the server requires approval, and only submit+poll to terminal when policy/approval allows. Default plan posture mutates nothing."),
            CreateTool(
                (string operationId, string reason)
                    => toolkit.RollbackGitOpsOperationAsync(operationId, reason),
                "rollback_gitops_operation",
                "Roll back a durable honua-server deploy-control operation to its prior known-good revision by operationId. Safety-gated: EXECUTION_MODE=plan (default) issues nothing; a data-affecting rollback (rollbackPlan.IsDataAffecting / non-MetadataOnly class, or unknown classification) ALWAYS requires explicit governed approval and is refused rather than auto-issued; only a non-data-affecting rollback under direct-allowed/break-glass actuates, and the server's OperatorApprovalGate is still honored (a 403 surfaces as approval-required). Never bypasses the approval gate."),
            CreateTool(
                (string customerRequirements, string scaleProfile, string complianceNeeds, string budgetProfile, string preferredCloud)
                    => toolkit.AnalyzeCustomerRequirementsAsync(customerRequirements, scaleProfile, complianceNeeds, budgetProfile, preferredCloud),
                "analyze_customer_requirements",
                "Analyze customer requirements and generate deployment recommendations."),
            CreateTool(
                (string environment, bool enableWaf, bool useNginxProxy, bool enableEdgeRateLimiting, string trafficProfile, string riskTolerance)
                    => toolkit.RecommendDeploymentTopologyAsync(environment, enableWaf, useNginxProxy, enableEdgeRateLimiting, trafficProfile, riskTolerance),
                "recommend_deployment_topology",
                "Recommend deployment topology options including WAF, ingress, and edge rate limiting."),
            CreateTool(
                (string ticketId, string severity, string environment, string symptoms, string requestedAction, string allowedAccessMode, int ttlMinutes, bool rollbackExpected, string attachedEvidence)
                    => toolkit.TriageSupportTicketAsync(ticketId, severity, environment, symptoms, requestedAction, allowedAccessMode, ttlMinutes, rollbackExpected, attachedEvidence),
                "triage_support_ticket",
                "Triage a support ticket: read-only diagnosis, guided-fix commands for the customer, or approval-gated operator-scoped remediation."),
            CreateTool(
                () => toolkit.ProcessPendingTicketsAsync(),
                "process_pending_tickets",
                "Pull pending support tickets from honua-support, run diagnosis against the fault catalog, and post results back."),
            CreateTool(
                (string ticketId, string severity, string environment, string symptoms, string requestedAction, string allowedAccessMode, int ttlMinutes, bool rollbackExpected, string attachedEvidence)
                    => toolkit.BuildSupportTicketConsoleViewAsync(ticketId, severity, environment, symptoms, requestedAction, allowedAccessMode, ttlMinutes, rollbackExpected, attachedEvidence),
                "get_support_ticket_console_view",
                "Build a Console-facing view of a support ticket's L2/L3 trust state: live delegated-session (access mode disabled/read-only/operator-scoped, effective TTL, absolute expiry, customer-visible and active flags), the DiagnosisScorecard (pass/fail, composite score, per-criterion checklist, failure modes, evidence), the escalation rationale (which signal/trigger caused the operator-scoped hand-off, or not-escalated), and audit-journal references. Read-only projection: runs the same diagnosis as triage but never opens a session, posts a diagnosis, or escalates."),
            CreateTool(
                (string service, string environment, string timeframe, string symptoms, string edition)
                    => toolkit.HonuaDiagnoseAsync(service, environment, timeframe, symptoms, edition),
                "honua_diagnose",
                "Run edition-aware read-only health diagnostics over Honua health, metrics, and error telemetry."),
            CreateTool(
                (string service, string environment, string timeframe, string slowQuerySample, string edition)
                    => toolkit.ExplainSlowQueriesAsync(service, environment, timeframe, slowQuerySample, edition),
                "honua_explain_slow_queries",
                "Explain slow query signatures and identify likely spatial, cache, or pool bottlenecks."),
            CreateTool(
                (string runbookName, string service, string environment, string parameters, bool confirmed, string edition)
                    => toolkit.RunbookExecuteAsync(runbookName, service, environment, parameters, confirmed, edition),
                "honua_runbook_execute",
                "Prepare or execute approved operational runbooks with Enterprise and execution-tier gates."),
            CreateTool(
                (string service, string environment, string detectedIssue, string desiredOutcome, bool autoApply, string edition)
                    => toolkit.AutoRemediationPlanAsync(service, environment, detectedIssue, desiredOutcome, autoApply, edition),
                "honua_auto_remediation_plan",
                "Plan Enterprise-gated self-healing actions with policy, approval, rollback, and validation controls."),
            CreateTool(
                (string workItemId, string kind, string currentState, string lowerEnvironment, string publishEnvironment, string previewUrl, string edition)
                    => toolkit.PlanDeliverableLifecycleAsync(workItemId, kind, currentState, lowerEnvironment, publishEnvironment, previewUrl, edition),
                "plan_deliverable_lifecycle",
                "Plan a deliverable's draft->preview->approved->published lifecycle (issue #77) bound to environments. Read-only planner: produces the lifecycle plan (ordered transitions, per-step target environment, gate, required evidence, edition requirement, and the governed Console approval action) and never generates the artifact, executes promotion, or mutates state. Single-environment draft->preview->approved is Pro; cross-environment approved->published (prod through deploy-control gated-promotion) is Enterprise — below the required edition the step is surfaced as edition-gated. Preview->Approved is emitted as a governed SuggestedAction (requiresApproval=true) via the Console approval surface; Approved->Published reuses the release-orchestration gated-promotion engine (approval-record + lower-env-evidence + smoke-contract + slo-gate-evidence). dryRun is always true."),
            CreateTool(
                (string service, string environmentsCsv, string revision, string action, string changeSummary, string owner)
                    => consoleBridge.CreateGitOpsProposalAsync(service, environmentsCsv, revision, action, changeSummary, owner),
                "create_gitops_proposal",
                "Create a Console-facing GitOps deployment proposal as a stable, evidence-linked projection. Records a durable honua-server deploy-control operation with submitImmediately=false (advisory; never executes) and returns the proposal with server operationId, deterministic idempotency key, raw backend evidence, workflow deep links, and governed submit/rollback suggestions. Stays blocked if no deploy target is configured."),
            CreateTool(
                (string operationId)
                    => consoleBridge.GetGitOpsProposalAsync(operationId),
                "get_gitops_proposal",
                "View an existing GitOps proposal by stable operationId as a projection over the honua-server deploy-control operation, with raw evidence references and governed suggestions. Never scrapes Git or CI."),
            CreateTool(
                (string operationId, string decision, string actor, string reason)
                    => consoleBridge.RecordProposalDecisionAsync(operationId, decision, actor, reason),
                "record_gitops_proposal_decision",
                "Record an auditable approve or reject decision against a GitOps proposal by stable operationId, capturing the deciding actor and a reason consistent with the honua-server decision audit. Moves the proposal's canonical lifecycle toward Submitted (approve) or Rejected (reject). Records only; never submits, executes, or rolls back. Approval surfaces the governed submit suggestion that still requires an explicit governed submit."),
            CreateTool(
                (string operationId)
                    => consoleBridge.GetDevOpsOperationStatusAsync(operationId),
                "get_devops_operation_status",
                "Get the unified DevOps operation status for a stable operationId across proposal, PR, CI, promotion, smoke, SLO-watch, rollback-readiness, and rollback-execution sections that all share the operationId. PR/CI sections are marked evidence-missing rather than scraped."),
            CreateTool(
                (string service, string environment, string title, string summary, string recommendedAction, string evidenceReference, string operationId, string confidence)
                    => consoleBridge.BuildAiDevOpsBriefAsync(service, environment, title, summary, recommendedAction, evidenceReference, operationId, confidence),
                "build_ai_devops_brief",
                "Build an advisory AI DevOps brief with affected resources, raw evidence references, suggested actions (with requiresApproval/mutatesState flags), confidence, owner, status, and workflow links. Advisory only with auto-apply disabled; mutating suggestions require an explicit governed submit/rollback."),
            CreateTool(
                (string releasePackageJson, string mode, string correlationId)
                    => releaseExplainer.ExplainReleasePackageAsync(releasePackageJson, mode, correlationId),
                "explain_release_package",
                "Explain a Console release package without computing compatibility or scraping Git/CI. Input is the release-package evidence JSON (server compatibility report, script coverage, PR preview, promotion gates, rollback plan). Returns a structured, evidence-linked explanation: per-section status, promotion gates, required approvals, residual risk, a rollback classification, and a human-readable summary, with overall readiness of ready/warning/blocked/unknown/rollback-required. mode is 'explanation' (read-only, default) or 'proposal' (governed handoff only; never executes). correlationId links the Console action to the server release package and downstream PR/CI/GitOps operations.")
        ];
    }

    private static AITool CreateTool(Delegate function, string name, string description)
    {
        return AIFunctionFactory.Create(
            function,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description
            });
    }
}
