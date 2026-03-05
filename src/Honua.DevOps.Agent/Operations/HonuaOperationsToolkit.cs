using System.ComponentModel;

namespace Honua.DevOps.Agent.Operations;

internal sealed class HonuaOperationsToolkit(OperationRuntime runtime, BackendGateway gateway)
{
    [Description("Analyze logs through OTEL endpoint and produce findings, remediation steps, and validation checks.")]
    public async Task<OperationResponse> AnalyzeLogsAsync(
        string service,
        string environment,
        string timeframe,
        string symptoms,
        string logSample,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.QueryLogsAsync(
            service,
            environment,
            timeframe,
            symptoms,
            logSample,
            cancellationToken);

        string scope = Scope(service, environment, timeframe);
        List<string> findings =
        [
            $"OTEL logs endpoint: {backendResult.Endpoint}",
            $"Backend result: {backendResult.Detail}",
            $"Response excerpt: {backendResult.PayloadPreview}"
        ];

        if (Contains(logSample, "timeout", "timed out"))
        {
            findings.Add("Timeout indicators present in provided sample.");
        }

        if (Contains(logSample, "connection", "pool"))
        {
            findings.Add("Connection or pool pressure indicators present in provided sample.");
        }

        if (!backendResult.IsSuccess)
        {
            findings.Add("Live log query failed. Validate OTEL endpoint path, auth key, and query payload contract.");
        }

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "analysis-ready" : "backend-error",
            Summary: $"Log analysis request for {scope}.",
            Findings: findings,
            Actions:
            [
                "Correlate errors by trace id and isolate first-failure boundary.",
                "Compare failing routes against slowest queries in the same window.",
                "Apply smallest corrective change and re-check SLOs."
            ],
            ValidationChecks:
            [
                "Error rate returns below baseline for at least one alert window.",
                "P95 latency and retry volume trend downward after mitigation."
            ],
            Risks:
            [
                "Incomplete log context can hide upstream root cause.",
                "Treating symptom-only signatures can cause recurrence."
            ]);
    }

    [Description("Analyze metrics through OTEL endpoint and return bottleneck findings with optimization priorities.")]
    public async Task<OperationResponse> AnalyzeMetricsAsync(
        string service,
        string environment,
        string timeframe,
        string objective,
        string metricSnapshot,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.QueryMetricsAsync(
            service,
            environment,
            timeframe,
            objective,
            metricSnapshot,
            cancellationToken);

        string scope = Scope(service, environment, timeframe);
        List<string> findings =
        [
            $"OTEL metrics endpoint: {backendResult.Endpoint}",
            $"Backend result: {backendResult.Detail}",
            $"Response excerpt: {backendResult.PayloadPreview}",
            $"Optimization objective: {Normalize(objective, "improve latency and stability")}."
        ];

        if (!backendResult.IsSuccess)
        {
            findings.Add("Live metric query failed. Verify OTEL metrics path and authentication configuration.");
        }

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "analysis-ready" : "backend-error",
            Summary: $"Metric analysis request for {scope}.",
            Findings: findings,
            Actions:
            [
                "Rank bottlenecks by user impact, not by raw utilization alone.",
                "Apply one tuning change at a time and capture before/after metrics.",
                "Promote validated tuning from dev -> staging -> prod via GitOps."
            ],
            ValidationChecks:
            [
                "SLO indicators improve without error-rate regression.",
                "Resource headroom remains above safety threshold."
            ],
            Risks:
            [
                "Burst windows can skew optimization decisions.",
                "Aggressive tuning can reduce failover resiliency."
            ]);
    }

    [Description("Generate a performance tuning plan via Honua API using workload and bottleneck inputs.")]
    public async Task<OperationResponse> TunePerformanceAsync(
        string service,
        string environment,
        string workloadProfile,
        string bottleneck,
        string targetSlo,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.RequestTuneAsync(
            service,
            environment,
            workloadProfile,
            bottleneck,
            targetSlo,
            cancellationToken);

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "plan-ready" : "backend-error",
            Summary: $"Performance tuning request for service `{service}` in `{environment}`.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Target SLO: {Normalize(targetSlo, "stabilize P95 latency and error budget")}."
            ],
            Actions:
            [
                "Tune data path first: query shape, index coverage, and filtering strategy.",
                "Tune runtime next: connection pool, cache behavior, and timeout policy.",
                "Roll out tuning with canary checks before broad promotion."
            ],
            ValidationChecks:
            [
                "P95/P99 latency improves under representative load.",
                "Throughput improves without saturation alarms."
            ],
            Risks:
            [
                "Over-indexing can increase write and maintenance costs.",
                "Cache-only tuning can hide underlying query inefficiency."
            ]);
    }

    [Description("Troubleshoot an incident through Honua API and return ordered response actions.")]
    public async Task<OperationResponse> TroubleshootIncidentAsync(
        string service,
        string environment,
        string incidentSummary,
        string suspectedComponent,
        string businessImpact,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.RequestTroubleshootAsync(
            service,
            environment,
            incidentSummary,
            suspectedComponent,
            businessImpact,
            cancellationToken);

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "triage-ready" : "backend-error",
            Summary: $"Incident triage request for `{service}` in `{environment}`.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Business impact: {Normalize(businessImpact, "unknown")}."
            ],
            Actions:
            [
                "Stabilize traffic and reduce blast radius before deep changes.",
                "Correlate deployment diff, logs, and metrics in the same incident window.",
                "Apply narrow corrective action and validate recovery before closure."
            ],
            ValidationChecks:
            [
                "User impact is reduced or eliminated.",
                "No new high-severity alerts in full observation window."
            ],
            Risks:
            [
                "Parallel uncoordinated mitigations can obscure root cause.",
                "Skipping rollback criteria can extend outage duration."
            ]);
    }

    [Description("Plan Honua server upgrades through Honua API with sequencing and rollback gates.")]
    public async Task<OperationResponse> PlanServerUpgradeAsync(
        string environment,
        string currentVersion,
        string targetVersion,
        string maintenanceWindow,
        string constraints,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.RequestUpgradeAsync(
            environment,
            currentVersion,
            targetVersion,
            maintenanceWindow,
            constraints,
            cancellationToken);

        return new OperationResponse(
            Status: backendResult.IsSuccess
                ? runtime.ExecutionMode == ExecutionMode.Execute ? "execute-enabled" : "plan-only"
                : "backend-error",
            Summary: $"Upgrade workflow from {Normalize(currentVersion, "current")} to {Normalize(targetVersion, "target")} for `{environment}`.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}"
            ],
            Actions:
            [
                "Validate compatibility and backup/restore path before first rollout.",
                "Run staged rollout dev -> staging -> prod with explicit stop gates.",
                "Keep rollback manifest and verify downgrade safety constraints."
            ],
            ValidationChecks:
            [
                "Post-upgrade smoke tests pass across all protocol entrypoints.",
                "No regression in latency, error-rate, or replication health."
            ],
            Risks:
            [
                "Schema drift can break rollback path.",
                "Bypassing staged promotion increases blast radius."
            ]);
    }

    [Description("Generate GitOps deployment actions across environments via Honua API using validated Terraform templates.")]
    public async Task<OperationResponse> DeployServiceWithGitOpsAsync(
        string service,
        string environmentsCsv,
        string revision,
        string action,
        string changeSummary,
        CancellationToken cancellationToken = default)
    {
        string[] targetEnvironments = ParseEnvironments(environmentsCsv);
        string normalizedRevision = Normalize(revision, "HEAD");
        string normalizedAction = Normalize(action, "sync");

        BackendCallResult backendResult = await gateway.RequestGitOpsDeployAsync(
            service,
            targetEnvironments,
            normalizedRevision,
            normalizedAction,
            changeSummary,
            runtime.GitOpsTool,
            runtime.TerraformRepository,
            runtime.TerraformRef,
            runtime.TerraformDeploymentTargets,
            cancellationToken);

        List<string> actionsList =
        [
            $"Use GitOps tool `{runtime.GitOpsTool}` for staged promotion: {string.Join(" -> ", targetEnvironments)}.",
            $"Source infrastructure templates from `{runtime.TerraformRepository}` at ref `{runtime.TerraformRef}`.",
            $"Validated deployment targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}.",
            "Use Honua GitOps primitives from honua-server issues #351/#363: apply, dryRun, prune, drift detection, approval gates.",
            "Enforce health and integration checks before promoting to next environment."
        ];
        actionsList.AddRange(BuildGitOpsCommands(service, targetEnvironments, normalizedRevision, normalizedAction));

        return new OperationResponse(
            Status: backendResult.IsSuccess
                ? runtime.ExecutionMode == ExecutionMode.Execute ? "execute-enabled" : "plan-only"
                : "backend-error",
            Summary: $"GitOps deployment plan for `{service}` across {string.Join(", ", targetEnvironments)}.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Change summary: {Normalize(changeSummary, "not provided")}."
            ],
            Actions: actionsList,
            ValidationChecks:
            [
                "Deployment diff matches approved scope and Terraform template version.",
                "Each environment passes validation before promotion."
            ],
            Risks:
            [
                "Environment drift can invalidate promotion assumptions.",
                "Template drift from validated Terraform repo can cause inconsistent infra state."
            ]);
    }

    [Description("Analyze customer requirements through Honua API and generate deployment recommendations.")]
    public async Task<OperationResponse> AnalyzeCustomerRequirementsAsync(
        string customerRequirements,
        string scaleProfile,
        string complianceNeeds,
        string budgetProfile,
        string preferredCloud,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.RequestRequirementsAnalysisAsync(
            customerRequirements,
            scaleProfile,
            complianceNeeds,
            budgetProfile,
            preferredCloud,
            runtime.TerraformRepository,
            runtime.TerraformRef,
            RecommendTargetsForCloud(preferredCloud),
            cancellationToken);

        string cloud = Normalize(preferredCloud, "cloud-agnostic");
        string[] recommendedTargets = RecommendTargetsForCloud(preferredCloud);
        return new OperationResponse(
            Status: backendResult.IsSuccess ? "solution-ready" : "backend-error",
            Summary: $"Deployment recommendation request targeting {cloud}.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Terraform template source: {runtime.TerraformRepository}@{runtime.TerraformRef}",
                $"Recommended deployment targets: {string.Join(", ", recommendedTargets)}"
            ],
            Actions:
            [
                "Map customer requirements to validated Terraform deployment templates.",
                "Select target runtime from validated set: azure-functions, lambda, eks, aks, ecs, aca.",
                "Select topology by risk and performance profile: WAF/no-WAF, nginx/no-proxy, edge rate limiting.",
                "Produce staged GitOps rollout with rollback and operational ownership."
            ],
            ValidationChecks:
            [
                $"Budget profile ({Normalize(budgetProfile, "balanced")}) aligns with recommended architecture.",
                "Proposed design satisfies required security and availability constraints."
            ],
            Risks:
            [
                "Missing non-functional requirements can produce under- or over-sized topology.",
                "Cost-only optimization can undercut resiliency for critical workloads."
            ]);
    }

    [Description("Recommend deployment topology via Honua API, including WAF, ingress, and edge rate limiting choices.")]
    public async Task<OperationResponse> RecommendDeploymentTopologyAsync(
        string environment,
        bool enableWaf,
        bool useNginxProxy,
        bool enableEdgeRateLimiting,
        string trafficProfile,
        string riskTolerance,
        CancellationToken cancellationToken = default)
    {
        BackendCallResult backendResult = await gateway.RequestTopologyRecommendationAsync(
            environment,
            enableWaf,
            useNginxProxy,
            enableEdgeRateLimiting,
            trafficProfile,
            riskTolerance,
            runtime.TerraformRepository,
            runtime.TerraformRef,
            cancellationToken);

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "plan-ready" : "backend-error",
            Summary: $"Topology recommendation for `{environment}`.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Terraform template source: {runtime.TerraformRepository}@{runtime.TerraformRef}",
                $"Validated deployment targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}"
            ],
            Actions:
            [
                $"WAF decision: {(enableWaf ? "enable with managed protections" : "disabled; enforce compensating controls at edge")}.",
                $"Ingress decision: {(useNginxProxy ? "nginx policy gateway" : "direct ingress with service-level policy controls")}.",
                $"Rate limiting: {(enableEdgeRateLimiting ? "enforce at edge" : "enforce via service policy + monitoring")}.",
                "Select matching Terraform template module and roll out through GitOps."
            ],
            ValidationChecks:
            [
                "Synthetic load test confirms latency and failure behavior targets.",
                "Security controls align with risk tolerance and compliance needs."
            ],
            Risks:
            [
                "No-WAF posture increases exposure to application-layer attacks.",
                "Skipping validated template modules can introduce config drift."
            ]);
    }

    private static string Scope(string service, string environment, string timeframe)
    {
        return $"service `{Normalize(service, "unknown")}` in `{Normalize(environment, "unknown")}` during `{Normalize(timeframe, "recent window")}`";
    }

    private static bool Contains(string value, params string[] probes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return probes.Any(probe => value.Contains(probe, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private string[] ParseEnvironments(string value)
    {
        string[] parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => runtime.AllowedEnvironments.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parsed.Length == 0 ? runtime.AllowedEnvironments : parsed;
    }

    private IEnumerable<string> BuildGitOpsCommands(
        string service,
        IEnumerable<string> environments,
        string revision,
        string action)
    {
        string tool = runtime.GitOpsTool.Trim().ToLowerInvariant();
        foreach (string environment in environments)
        {
            yield return tool switch
            {
                "honua-gitops" => $"Suggested command ({environment}): honua gitops {action} --service {service} --env {environment} --revision {revision}",
                "flux" => $"Suggested command ({environment}): flux reconcile kustomization {service}-{environment} --with-source",
                "argocd" => $"Suggested command ({environment}): argocd app sync {service}-{environment} --revision {revision}",
                _ => $"Suggested command ({environment}): {runtime.GitOpsTool} sync {service}-{environment} --revision {revision}"
            };
        }
    }

    private string[] RecommendTargetsForCloud(string? preferredCloud)
    {
        if (string.IsNullOrWhiteSpace(preferredCloud))
        {
            return runtime.TerraformDeploymentTargets;
        }

        string cloud = preferredCloud.Trim().ToLowerInvariant();
        if (cloud.Contains("azure", StringComparison.Ordinal))
        {
            return runtime.TerraformDeploymentTargets
                .Where(target => target is "aks" or "aca" or "azure-functions")
                .ToArray();
        }

        if (cloud.Contains("aws", StringComparison.Ordinal) || cloud.Contains("amazon", StringComparison.Ordinal))
        {
            return runtime.TerraformDeploymentTargets
                .Where(target => target is "eks" or "ecs" or "lambda")
                .ToArray();
        }

        return runtime.TerraformDeploymentTargets;
    }
}
