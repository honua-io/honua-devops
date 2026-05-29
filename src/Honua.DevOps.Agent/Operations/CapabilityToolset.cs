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
                (string service, string environmentsCsv, string revision, string action, string changeSummary)
                    => toolkit.DeployServiceWithGitOpsAsync(service, environmentsCsv, revision, action, changeSummary),
                "deploy_service_gitops",
                "Generate GitOps deployment actions across environments."),
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
                () => toolkit.TriagePendingTicketsAsync(),
                "triage_pending_tickets",
                "Pull pending/open support tickets from honua-support and emit a planning-only triage plan (per-ticket severity, category, suggested next action, confidence, priority). Read-only: posts nothing and takes no remediation; fixes stay behind approval/execution gates."),
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
                "Plan Enterprise-gated self-healing actions with policy, approval, rollback, and validation controls.")
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
