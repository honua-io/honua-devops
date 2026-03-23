using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Microsoft.Extensions.AI;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

internal static class CapabilityToolset
{
    internal static IList<AITool> Create(OperationRuntime runtime, BackendGateway gateway, OperatorPolicyModel? policy = null, SupportGateway? supportGateway = null)
    {
        HonuaOperationsToolkit toolkit = new(runtime, gateway, policy, supportGateway);

        return
        [
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
                "Pull pending support tickets from honua-support, run diagnosis against the fault catalog, and post results back.")
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
