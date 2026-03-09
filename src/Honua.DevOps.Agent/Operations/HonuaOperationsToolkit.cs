using System.ComponentModel;

using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

internal sealed class HonuaOperationsToolkit(OperationRuntime runtime, BackendGateway gateway, OperatorPolicyModel? policy = null)
{
    private OperatorPolicyModel EffectivePolicy => policy ?? OperatorPolicyModel.Default;

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
        RuntimeAdapterRequest adapterRequest = new(
            Service: "honua-server",
            Environments: [environment],
            Revision: Normalize(targetVersion, "target"),
            Action: "upgrade",
            ChangeSummary: $"upgrade from {Normalize(currentVersion, "current")} to {Normalize(targetVersion, "target")}",
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            TerraformLocalPath: runtime.TerraformLocalPath,
            DryRun: runtime.ExecutionMode != ExecutionMode.Execute,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        IReadOnlyList<RuntimeAdapterWorkflow> upgradeWorkflows = RuntimeAdapterRegistry.ResolveMany(runtime.TerraformDeploymentTargets)
            .Select(adapter => adapter.BuildWorkflow(adapterRequest))
            .ToArray();
        ReleaseOrchestrationPlan releaseOrchestration = ReleaseOrchestrationPlanner.Build(
            upgradeWorkflows,
            [environment],
            "upgrade",
            runtime.ExecutionMode != ExecutionMode.Execute,
            runtime.ExecutionMode != ExecutionMode.Execute ? "plan-only-upgrade" : "staged-rollout-required");
        List<string> upgradeActions =
        [
            "Validate compatibility and backup/restore path before first rollout.",
            "Run staged rollout dev -> staging -> prod with explicit stop gates.",
            "Keep rollback manifest and verify downgrade safety constraints."
        ];
        upgradeActions.AddRange(BuildOperatorPolicyActions());
        upgradeActions.AddRange(BuildReleaseOrchestrationActions(releaseOrchestration));
        List<string> upgradeValidationChecks =
        [
            "Post-upgrade smoke tests pass across all protocol entrypoints.",
            "No regression in latency, error-rate, or replication health."
        ];
        upgradeValidationChecks.AddRange(ReleaseOrchestrationPlanner.FlattenEvidenceRequirements(releaseOrchestration));

        return new OperationResponse(
            Status: backendResult.IsSuccess
                ? runtime.ExecutionMode == ExecutionMode.Execute ? "execute-enabled" : "plan-only"
                : "backend-error",
            Summary: $"Upgrade workflow from {Normalize(currentVersion, "current")} to {Normalize(targetVersion, "target")} for `{environment}`.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Release strategy: {releaseOrchestration.Strategy}.",
                $"Migration mode: {releaseOrchestration.MigrationMode}.",
                $"Approval mode: {EffectivePolicy.ApprovalMode.ToConfigValue()}.",
                $"Audit hook target: {EffectivePolicy.AuditHookTarget}."
            ],
            Actions: upgradeActions,
            ValidationChecks: upgradeValidationChecks,
            Risks:
            [
                "Schema drift can break rollback path.",
                "Bypassing staged promotion increases blast radius."
            ],
            Evidence: BuildUpgradeEvidence(
                environment,
                currentVersion,
                targetVersion,
                releaseOrchestration,
                backendResult),
            ReleaseOrchestration: releaseOrchestration);
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
        string normalizedService = ValidateServiceName(service);
        string[] targetEnvironments = ParseEnvironments(environmentsCsv);
        string normalizedRevision = ValidateRevision(Normalize(revision, "HEAD"), "revision");
        string normalizedAction = ValidateAction(action);
        string normalizedChangeSummary = SanitizeFreeText(changeSummary, "not provided");
        string normalizedGitOpsTool = ValidateGitOpsTool(runtime.GitOpsTool);
        string normalizedTerraformRepository = SanitizePayloadValue(runtime.TerraformRepository, "terraform repository");
        string normalizedTerraformRef = ValidateRevision(runtime.TerraformRef, "terraform ref");
        string[] normalizedDeploymentTargets = ValidateDeploymentTargets(runtime.TerraformDeploymentTargets);
        DeploymentAuthorization authorization = AuthorizeDeployment(targetEnvironments, normalizedAction);
        RuntimeAdapterRequest adapterRequest = new(
            Service: normalizedService,
            Environments: targetEnvironments,
            Revision: normalizedRevision,
            Action: normalizedAction,
            ChangeSummary: normalizedChangeSummary,
            GitOpsTool: normalizedGitOpsTool,
            TerraformRepository: normalizedTerraformRepository,
            TerraformRef: normalizedTerraformRef,
            TerraformLocalPath: runtime.TerraformLocalPath,
            DryRun: authorization.DryRun,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows = RuntimeAdapterRegistry.ResolveMany(normalizedDeploymentTargets)
            .Select(adapter => adapter.BuildWorkflow(adapterRequest))
            .ToArray();
        IReadOnlyList<RuntimeAdapterCapability> adapterCapabilities = adapterWorkflows
            .Select(workflow => workflow.Capability)
            .ToArray();
        using GitOpsDeployBackendResult backendResult = await gateway.RequestGitOpsDeployAsync(
            normalizedService,
            targetEnvironments,
            normalizedRevision,
            normalizedAction,
            normalizedChangeSummary,
            normalizedGitOpsTool,
            normalizedTerraformRepository,
            normalizedTerraformRef,
            normalizedDeploymentTargets,
            authorization.DryRun,
            runtime.ExecutionMode,
            runtime.ExecutionTier,
            runtime.AllowedEnvironments,
            cancellationToken);
        ReleaseOrchestrationPlan releaseOrchestration = ReleaseOrchestrationPlanner.Build(
            adapterWorkflows,
            targetEnvironments,
            normalizedAction,
            authorization.DryRun,
            authorization.PolicyGate);
        ServiceBundleReconciliationPlan serviceBundleReconciliation = ServiceBundleReconciliationPlanner.Build(
            normalizedService,
            targetEnvironments,
            gateway.Configuration,
            backendResult.CapabilitiesPayload,
            backendResult.CapabilitiesResult.PayloadPreview,
            backendResult.ExportPayload,
            backendResult.ExportResult.PayloadPreview);
        GitOpsPlan gitOpsPlan = GitOpsPlanner.Build(
            normalizedService,
            targetEnvironments,
            normalizedRevision,
            normalizedAction,
            normalizedGitOpsTool,
            authorization.DryRun,
            authorization.PolicyGate,
            backendResult,
            releaseOrchestration,
            serviceBundleReconciliation);

        List<string> actionsList =
        [
            authorization.WorkflowGuidance,
            $"Use GitOps tool `{normalizedGitOpsTool}` for staged promotion: {string.Join(" -> ", targetEnvironments)}.",
            $"Source infrastructure templates from `{normalizedTerraformRepository}` at ref `{normalizedTerraformRef}`.",
            $"Validated deployment targets: {string.Join(", ", normalizedDeploymentTargets)}.",
            $"Runtime adapter capability matrix: {string.Join(" | ", adapterCapabilities.Select(capability => capability.ToSummary()))}.",
            "Use Honua GitOps primitives from honua-server issues #351/#363: apply, dryRun, prune, drift detection, approval gates.",
            "Enforce health and integration checks before promoting to next environment."
        ];
        actionsList.AddRange(BuildOperatorPolicyActions());
        actionsList.AddRange(BuildRuntimeAdapterActions(adapterWorkflows, authorization.DryRun));
        actionsList.AddRange(BuildReleaseOrchestrationActions(releaseOrchestration));
        actionsList.AddRange(BuildServiceBundleReconciliationActions(serviceBundleReconciliation));
        actionsList.AddRange(BuildGitOpsPlanActions(gitOpsPlan));

        return new OperationResponse(
            Status: backendResult.CombinedResult.IsSuccess
                ? runtime.ExecutionMode == ExecutionMode.Execute ? "execute-enabled" : "plan-only"
                : "backend-error",
            Summary: $"GitOps deployment plan for `{normalizedService}` across {string.Join(", ", targetEnvironments)}.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.CombinedResult.Endpoint}",
                $"Backend result: {backendResult.CombinedResult.Detail}",
                $"Response excerpt: {backendResult.CombinedResult.PayloadPreview}",
                $"Change summary: {normalizedChangeSummary}.",
                $"Runtime adapter families: {string.Join(", ", adapterCapabilities.Select(capability => $"{capability.Target}={capability.Family}"))}.",
                $"Release strategy: {releaseOrchestration.Strategy}.",
                $"Migration mode: {releaseOrchestration.MigrationMode}.",
                $"Promotion gate: {releaseOrchestration.PromotionPolicy.Gate}.",
                $"Rollback fallback mode: {releaseOrchestration.RollbackPolicy.FallbackMode}.",
                $"ServiceBundle reconciliation strategy: {serviceBundleReconciliation.Strategy}.",
                $"ServiceBundle export mode: {serviceBundleReconciliation.ExportMode}.",
                $"ServiceBundle current state: {serviceBundleReconciliation.CurrentStateSummary}.",
                $"GitOps diff summary: {gitOpsPlan.DiffSummary}.",
                $"GitOps drift summary: {gitOpsPlan.DriftSummary}.",
                $"GitOps actual state source: {gitOpsPlan.ActualStateSource}.",
                $"Approval mode: {EffectivePolicy.ApprovalMode.ToConfigValue()}.",
                $"Audit hook target: {EffectivePolicy.AuditHookTarget}.",
                $"Support session access: {EffectivePolicy.SupportSession.Access.ToConfigValue()} ({EffectivePolicy.SupportSession.TtlMinutes}m TTL).",
                $"Execution tier `{runtime.ExecutionTier.ToConfigValue()}` resolved to {(authorization.DryRun ? "dry-run" : "write-enabled")} behavior."
            ],
            Actions: actionsList,
            ValidationChecks:
            [
                authorization.ValidationGuidance,
                ..BuildOperatorPolicyValidationChecks(),
                ..BuildRuntimeAdapterValidationChecks(adapterWorkflows),
                ..gitOpsPlan.RequiredEvidence,
                ..ReleaseOrchestrationPlanner.FlattenEvidenceRequirements(releaseOrchestration),
                ..ServiceBundleReconciliationPlanner.FlattenEvidenceRequirements(serviceBundleReconciliation),
                "Deployment diff matches approved scope and Terraform template version.",
                "Each environment passes validation before promotion."
            ],
            Risks:
            [
                authorization.RiskGuidance,
                ..BuildOperatorPolicyRisks(),
                ..BuildRuntimeAdapterRisks(adapterWorkflows),
                ..releaseOrchestration.PromotionPolicy.Blockers.Select(blocker => $"Promotion blocker: {blocker}."),
                ..releaseOrchestration.RollbackPolicy.Triggers.Select(trigger => $"Rollback trigger: {trigger}."),
                "Environment drift can invalidate promotion assumptions.",
                "Template drift from validated Terraform repo can cause inconsistent infra state."
            ],
            Evidence: BuildDeploymentEvidence(
                normalizedTerraformRepository,
                normalizedTerraformRef,
                normalizedDeploymentTargets,
                adapterWorkflows,
                releaseOrchestration,
                serviceBundleReconciliation,
                gitOpsPlan,
                authorization,
                backendResult.CombinedResult),
            GitOpsPlan: gitOpsPlan,
            ReleaseOrchestration: releaseOrchestration,
            ServiceBundleReconciliation: serviceBundleReconciliation);
    }

    [Description("Plan the internal honua-gitops engine state transitions, diff, and drift without applying desired state.")]
    public async Task<OperationResponse> PlanGitOpsEngineAsync(
        string service,
        string environmentsCsv,
        string revision,
        string action,
        string changeSummary,
        CancellationToken cancellationToken = default)
    {
        string normalizedService = ValidateServiceName(service);
        string[] targetEnvironments = ParseEnvironments(environmentsCsv);
        string normalizedRevision = ValidateRevision(Normalize(revision, "HEAD"), "revision");
        string normalizedAction = ValidateAction(action);
        string normalizedChangeSummary = SanitizeFreeText(changeSummary, "not provided");
        string normalizedGitOpsTool = ValidateGitOpsTool(runtime.GitOpsTool);
        string normalizedTerraformRepository = SanitizePayloadValue(runtime.TerraformRepository, "terraform repository");
        string normalizedTerraformRef = ValidateRevision(runtime.TerraformRef, "terraform ref");
        string[] normalizedDeploymentTargets = ValidateDeploymentTargets(runtime.TerraformDeploymentTargets);
        DeploymentAuthorization authorization = AuthorizeDeployment(targetEnvironments, normalizedAction) with
        {
            DryRun = true,
            PolicyGate = "gitops-engine-plan",
            WorkflowGuidance = "Plan the internal honua-gitops engine only; do not apply desired state in this path."
        };

        RuntimeAdapterRequest adapterRequest = new(
            Service: normalizedService,
            Environments: targetEnvironments,
            Revision: normalizedRevision,
            Action: normalizedAction,
            ChangeSummary: normalizedChangeSummary,
            GitOpsTool: normalizedGitOpsTool,
            TerraformRepository: normalizedTerraformRepository,
            TerraformRef: normalizedTerraformRef,
            TerraformLocalPath: runtime.TerraformLocalPath,
            DryRun: true,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows = RuntimeAdapterRegistry.ResolveMany(normalizedDeploymentTargets)
            .Select(adapter => adapter.BuildWorkflow(adapterRequest))
            .ToArray();
        IReadOnlyList<RuntimeAdapterCapability> adapterCapabilities = adapterWorkflows
            .Select(workflow => workflow.Capability)
            .ToArray();

        using GitOpsDeployBackendResult backendResult = await gateway.PlanGitOpsRunAsync(cancellationToken);
        ReleaseOrchestrationPlan releaseOrchestration = ReleaseOrchestrationPlanner.Build(
            adapterWorkflows,
            targetEnvironments,
            normalizedAction,
            dryRun: true,
            authorization.PolicyGate);
        ServiceBundleReconciliationPlan serviceBundleReconciliation = ServiceBundleReconciliationPlanner.Build(
            normalizedService,
            targetEnvironments,
            gateway.Configuration,
            backendResult.CapabilitiesPayload,
            backendResult.CapabilitiesResult.PayloadPreview,
            backendResult.ExportPayload,
            backendResult.ExportResult.PayloadPreview);
        GitOpsPlan gitOpsPlan = GitOpsPlanner.Build(
            normalizedService,
            targetEnvironments,
            normalizedRevision,
            normalizedAction,
            normalizedGitOpsTool,
            dryRun: true,
            authorization.PolicyGate,
            backendResult,
            releaseOrchestration,
            serviceBundleReconciliation);

        List<string> actionsList =
        [
            authorization.WorkflowGuidance,
            $"Inspect internal `{normalizedGitOpsTool}` transitions before any apply path for {string.Join(" -> ", targetEnvironments)}.",
            $"Source infrastructure templates from `{normalizedTerraformRepository}` at ref `{normalizedTerraformRef}`.",
            $"Validated deployment targets: {string.Join(", ", normalizedDeploymentTargets)}.",
            $"Runtime adapter capability matrix: {string.Join(" | ", adapterCapabilities.Select(capability => capability.ToSummary()))}.",
            "Use this plan-only engine output to review diff, drift, and approval transitions before deploy_service_gitops."
        ];
        actionsList.AddRange(BuildOperatorPolicyActions());
        actionsList.AddRange(BuildRuntimeAdapterActions(adapterWorkflows, dryRun: true));
        actionsList.AddRange(BuildReleaseOrchestrationActions(releaseOrchestration));
        actionsList.AddRange(BuildServiceBundleReconciliationActions(serviceBundleReconciliation));
        actionsList.AddRange(BuildGitOpsPlanActions(gitOpsPlan));

        return new OperationResponse(
            Status: backendResult.CombinedResult.IsSuccess ? "gitops-engine-plan" : "backend-error",
            Summary: $"honua-gitops engine plan for `{normalizedService}` across {string.Join(", ", targetEnvironments)}.",
            Findings:
            [
                $"Honua API endpoint: {backendResult.CombinedResult.Endpoint}",
                $"Backend result: {backendResult.CombinedResult.Detail}",
                $"Response excerpt: {backendResult.CombinedResult.PayloadPreview}",
                $"Change summary: {normalizedChangeSummary}.",
                $"Runtime adapter families: {string.Join(", ", adapterCapabilities.Select(capability => $"{capability.Target}={capability.Family}"))}.",
                $"Release strategy: {releaseOrchestration.Strategy}.",
                $"ServiceBundle current state: {serviceBundleReconciliation.CurrentStateSummary}.",
                $"GitOps diff summary: {gitOpsPlan.DiffSummary}.",
                $"GitOps drift summary: {gitOpsPlan.DriftSummary}.",
                $"GitOps actual state source: {gitOpsPlan.ActualStateSource}."
            ],
            Actions: actionsList,
            ValidationChecks:
            [
                authorization.ValidationGuidance,
                ..BuildOperatorPolicyValidationChecks(),
                ..BuildRuntimeAdapterValidationChecks(adapterWorkflows),
                ..gitOpsPlan.RequiredEvidence,
                ..ReleaseOrchestrationPlanner.FlattenEvidenceRequirements(releaseOrchestration),
                ..ServiceBundleReconciliationPlanner.FlattenEvidenceRequirements(serviceBundleReconciliation),
                "Review state transitions before approving any write-capable GitOps path."
            ],
            Risks:
            [
                authorization.RiskGuidance,
                ..BuildOperatorPolicyRisks(),
                ..BuildRuntimeAdapterRisks(adapterWorkflows),
                "Snapshot-only planning can drift from reality if actual state changes before apply.",
                ..releaseOrchestration.PromotionPolicy.Blockers.Select(blocker => $"Promotion blocker: {blocker}.")
            ],
            Evidence: BuildDeploymentEvidence(
                normalizedTerraformRepository,
                normalizedTerraformRef,
                normalizedDeploymentTargets,
                adapterWorkflows,
                releaseOrchestration,
                serviceBundleReconciliation,
                gitOpsPlan,
                authorization,
                backendResult.CombinedResult),
            GitOpsPlan: gitOpsPlan,
            ReleaseOrchestration: releaseOrchestration,
            ServiceBundleReconciliation: serviceBundleReconciliation);
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
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Deployment environments are required and must match the configured allowed environment list.");
        }

        string[] requested = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length == 0)
        {
            throw new InvalidOperationException(
                "Deployment environments are required and must not be empty.");
        }

        string[] invalid = requested
            .Where(item => !runtime.AllowedEnvironments.Contains(item, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(
                $"Invalid deployment environments: {string.Join(", ", invalid)}. Allowed values: {string.Join(", ", runtime.AllowedEnvironments)}.");
        }

        return requested;
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

    private static string ValidateServiceName(string value)
    {
        string service = Normalize(value, string.Empty);
        if (service.Length is < 1 or > 80)
        {
            throw new InvalidOperationException("Service name must be 1-80 characters.");
        }

        if (!char.IsLetterOrDigit(service[0]))
        {
            throw new InvalidOperationException("Service name must start with a letter or digit.");
        }

        if (service.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException(
                "Service name contains invalid characters. Allowed characters: letters, numbers, '-', '_', '.'.");
        }

        return service;
    }

    private static string ValidateRevision(string value, string fieldName)
    {
        string revision = Normalize(value, "HEAD");
        if (revision.Length is < 1 or > 128)
        {
            throw new InvalidOperationException($"{fieldName} must be 1-128 characters.");
        }

        if (revision.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{fieldName} must not contain whitespace.");
        }

        if (revision.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or '@' or ':')))
        {
            throw new InvalidOperationException(
                $"{fieldName} contains invalid characters.");
        }

        return revision;
    }

    private static string ValidateAction(string value)
    {
        string normalized = Normalize(value, "sync").ToLowerInvariant();
        return normalized switch
        {
            "sync" => "sync",
            "apply" => "apply",
            "prune" => "prune",
            "dry-run" => "dry-run",
            "dryrun" => "dry-run",
            "plan" => "plan",
            "promote" => "promote",
            _ => throw new InvalidOperationException(
                $"Invalid deployment action `{value}`. Allowed values: sync, apply, prune, dry-run, plan, promote.")
        };
    }

    private DeploymentAuthorization AuthorizeDeployment(
        IReadOnlyList<string> targetEnvironments,
        string action)
    {
        bool targetsProd = targetEnvironments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        bool requestedDryRun = action is "plan" or "dry-run";
        bool executionEnabled = runtime.ExecutionMode == ExecutionMode.Execute && !requestedDryRun;
        OperatorPolicyModel policy = EffectivePolicy;

        if (executionEnabled)
        {
            switch (policy.ApprovalMode)
            {
                case ApprovalMode.PrFirst when runtime.ExecutionTier != ExecutionTier.BreakGlass:
                    throw new InvalidOperationException(
                        "Approval mode `pr-first` blocks direct execution. Use plan/propose flow or set approval mode to `direct-allowed`.");
                case ApprovalMode.BreakGlassOnly when runtime.ExecutionTier != ExecutionTier.BreakGlass:
                    throw new InvalidOperationException(
                        "Approval mode `break-glass-only` allows direct execution only in the `break-glass` tier.");
            }
        }

        return runtime.ExecutionTier switch
        {
            ExecutionTier.Observe => new DeploymentAuthorization(
                DryRun: true,
                PolicyGate: "observe-only",
                RequiredChecks:
                [
                    "telemetry-snapshot",
                    "drift-review"
                ],
                WorkflowGuidance: "Observe tier is read-only; emit evidence and hand the change off for proposal or execution.",
                ValidationGuidance: "Capture current health, drift, and release evidence without mutating any environment.",
                RiskGuidance: "Read-only evidence can still be incomplete if backend telemetry is stale."),
            ExecutionTier.Plan => new DeploymentAuthorization(
                DryRun: true,
                PolicyGate: "plan-only",
                RequiredChecks:
                [
                    "manifest-diff",
                    "target-validation"
                ],
                WorkflowGuidance: "Plan tier produces dry-run output only; use it to validate scope before any write-capable tier runs.",
                ValidationGuidance: "Review the diff, runtime target, and environment sequence before approval.",
                RiskGuidance: "A correct plan can still fail later if environment drift changes after approval."),
            ExecutionTier.Propose => new DeploymentAuthorization(
                DryRun: true,
                PolicyGate: "proposal-required",
                RequiredChecks:
                [
                    "manifest-diff",
                    "approval-context"
                ],
                WorkflowGuidance: "Propose tier prepares desired state and approval-ready evidence, but does not execute writes.",
                ValidationGuidance: "Capture the desired change and approval context in Git or the operator review flow.",
                RiskGuidance: "Proposal-only flows can drift from reality if they are executed long after creation."),
            ExecutionTier.ExecuteLowerEnv when targetsProd => throw new InvalidOperationException(
                "Execution tier `execute-lower-env` cannot target `prod`. Use `promote-prod` or `break-glass` for production changes."),
            ExecutionTier.ExecuteLowerEnv => new DeploymentAuthorization(
                DryRun: !executionEnabled,
                PolicyGate: executionEnabled ? "lower-env-execution" : "lower-env-plan-only",
                RequiredChecks:
                [
                    "manifest-diff",
                    "smoke-contract",
                    "release-evidence"
                ],
                WorkflowGuidance: "Execute-lower-env tier may write to non-prod environments after validation gates are satisfied.",
                ValidationGuidance: "Require lower-environment smoke and evidence capture before requesting prod promotion.",
                RiskGuidance: "Skipping staging evidence weakens the later prod promotion decision."),
            ExecutionTier.PromoteProd when targetsProd && action != "promote" => throw new InvalidOperationException(
                "Execution tier `promote-prod` requires action `promote` when targeting `prod`."),
            ExecutionTier.PromoteProd => new DeploymentAuthorization(
                DryRun: !executionEnabled,
                PolicyGate: targetsProd ? "prod-promotion-gated" : "promotion-prep",
                RequiredChecks:
                [
                    "lower-env-evidence",
                    "smoke-contract",
                    "release-evidence",
                    "approval-record"
                ],
                WorkflowGuidance: targetsProd
                    ? "Promote-prod tier is active; only a validated promotion into `prod` should execute."
                    : "Promote-prod tier is active, but this request only targets lower environments.",
                ValidationGuidance: targetsProd
                    ? "Require approved lower-environment evidence and release validation before prod promotion."
                    : "Validate lower environments before opening a prod promotion request.",
                RiskGuidance: targetsProd
                    ? "Prod promotion without validated release evidence increases rollback risk."
                    : "Using prod-promotion credentials for non-prod work can blur audit boundaries."),
            ExecutionTier.BreakGlass => new DeploymentAuthorization(
                DryRun: !executionEnabled,
                PolicyGate: executionEnabled ? "break-glass" : "break-glass-plan-only",
                RequiredChecks:
                [
                    "incident-context",
                    "operator-justification",
                    "rollback-intent"
                ],
                WorkflowGuidance: "Break-glass tier may execute directly, but only for exceptional recovery or urgent operator action.",
                ValidationGuidance: "Record incident context, operator justification, and rollback intent with the execution evidence.",
                RiskGuidance: "Break-glass bypasses normal guardrails and should be treated as elevated operational risk."),
            _ => throw new InvalidOperationException("Unsupported execution tier.")
        };
    }

    private static string ValidateGitOpsTool(string value)
    {
        string tool = Normalize(value, "honua-gitops");
        if (tool.Length is < 1 or > 64)
        {
            throw new InvalidOperationException("GitOps tool name must be 1-64 characters.");
        }

        if (tool.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/')))
        {
            throw new InvalidOperationException(
                "GitOps tool contains invalid characters.");
        }

        return tool;
    }

    private static string SanitizePayloadValue(string value, string fieldName)
    {
        string sanitized = SanitizeFreeText(value, string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException($"{fieldName} must not be empty.");
        }

        return sanitized;
    }

    private static string SanitizeFreeText(string? value, string fallback)
    {
        string normalized = Normalize(value, fallback);
        char[] filtered = normalized
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .ToArray();
        string compact = new string(filtered).Trim();
        if (compact.Length == 0)
        {
            return fallback;
        }

        const int maxLength = 600;
        return compact.Length <= maxLength
            ? compact
            : compact[..maxLength];
    }

    private static string[] ValidateDeploymentTargets(IEnumerable<string> targets)
    {
        string[] sanitized = targets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sanitized.Length == 0)
        {
            throw new InvalidOperationException("Terraform deployment targets must not be empty.");
        }

        string[] invalid = sanitized
            .Where(target =>
                target.Length > 60 ||
                !char.IsLetterOrDigit(target[0]) ||
                target.Any(character =>
                    !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            .ToArray();

        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(
                $"Terraform deployment targets contain invalid values: {string.Join(", ", invalid)}.");
        }

        return sanitized;
    }

    private static IEnumerable<string> BuildRuntimeAdapterActions(
        IEnumerable<RuntimeAdapterWorkflow> workflows,
        bool dryRun)
    {
        foreach (RuntimeAdapterWorkflow workflow in workflows)
        {
            yield return $"Adapter validate ({workflow.Capability.Target}): {workflow.Validate.Summary}";
            yield return $"Adapter plan infra ({workflow.Capability.Target}): {workflow.PlanInfrastructure.SuggestedCommands.First()}";
            yield return dryRun
                ? $"Adapter plan release ({workflow.Capability.Target}): {workflow.PlanRelease.SuggestedCommands.First()}"
                : $"Adapter apply release ({workflow.Capability.Target}): {workflow.ApplyRelease.SuggestedCommands.First()}";
            yield return $"Adapter verify ({workflow.Capability.Target}): {workflow.Verify.SuggestedCommands.First()}";
            yield return $"Adapter rollback ({workflow.Capability.Target}): {workflow.Rollback.SuggestedCommands.First()}";
        }
    }

    private IEnumerable<string> BuildOperatorPolicyActions()
    {
        yield return $"Operator policy approval mode: `{EffectivePolicy.ApprovalMode.ToConfigValue()}`.";
        yield return $"Audit hook target: `{EffectivePolicy.AuditHookTarget}`.";
        yield return $"Support session access: `{EffectivePolicy.SupportSession.Access.ToConfigValue()}` with TTL `{EffectivePolicy.SupportSession.TtlMinutes}` minutes and customer-visible `{EffectivePolicy.SupportSession.CustomerVisible}`.";

        if (EffectivePolicy.BreakGlassPostActionReviewRequired)
        {
            yield return "Break-glass actions require post-action review.";
        }
    }

    private IEnumerable<string> BuildOperatorPolicyValidationChecks()
    {
        yield return "approval-policy";
        yield return "audit-hook";

        if (EffectivePolicy.SupportSession.Access != SupportSessionAccess.Disabled)
        {
            yield return "support-session-ttl";
        }

        if (EffectivePolicy.BreakGlassPostActionReviewRequired)
        {
            yield return "post-action-review";
        }
    }

    private IEnumerable<string> BuildOperatorPolicyRisks()
    {
        if (EffectivePolicy.ApprovalMode == ApprovalMode.DirectAllowed)
        {
            yield return "Direct execution policy increases the chance of bypassing review compared to PR-first posture.";
        }

        if (EffectivePolicy.SupportSession.Access != SupportSessionAccess.Disabled)
        {
            yield return "Delegated support access must stay scoped and time-bound to avoid silent privilege expansion.";
        }
    }

    private static IEnumerable<string> BuildRuntimeAdapterValidationChecks(IEnumerable<RuntimeAdapterWorkflow> workflows)
    {
        return workflows
            .SelectMany(workflow => workflow.Verify.ValidationChecks)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildRuntimeAdapterRisks(IEnumerable<RuntimeAdapterWorkflow> workflows)
    {
        return workflows
            .SelectMany(workflow => workflow.Rollback.Risks)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildReleaseOrchestrationActions(ReleaseOrchestrationPlan plan)
    {
        yield return $"Promotion policy `{plan.PromotionPolicy.Gate}` requires: {string.Join(", ", plan.PromotionPolicy.RequiredEvidence)}";
        yield return $"Rollback policy `{plan.RollbackPolicy.FallbackMode}` triggers on: {string.Join(", ", plan.RollbackPolicy.Triggers)}";

        foreach (ReleasePromotionStep step in plan.PromotionSequence)
        {
            string path = step.SourceEnvironment is null
                ? $"bootstrap -> {step.TargetEnvironment}"
                : $"{step.SourceEnvironment} -> {step.TargetEnvironment}";
            yield return $"Promotion sequence `{path}` via gate `{step.Gate}` requires: {string.Join(", ", step.RequiredEvidence)}";
        }

        foreach (ReleaseStagePlan stage in plan.Stages)
        {
            yield return $"Release stage `{stage.Kind.ToConfigValue()}` ({stage.ExecutionCondition}): {stage.Summary}";
        }

        foreach (ReleaseRollbackSemanticsPlan rollback in plan.RollbackSemantics)
        {
            yield return $"Rollback `{rollback.ChangeClass}`: {rollback.RecoveryPath}";
        }
    }

    private static IEnumerable<string> BuildServiceBundleReconciliationActions(ServiceBundleReconciliationPlan plan)
    {
        yield return $"ServiceBundle export mode: `{plan.ExportMode}` with long-running handling `{plan.LongRunningOperationMode}`.";
        yield return $"ServiceBundle current state: {plan.CurrentStateSummary}";

        foreach (ServiceBundleDriftScope scope in plan.DriftScopes)
        {
            yield return $"ServiceBundle drift `{scope.Scope}`: export `{scope.ExportSource}` -> compare via `{scope.ComparisonMode}`";
        }

        foreach (ServiceBundleReconciliationOperation operation in plan.Operations)
        {
            yield return $"ServiceBundle reconcile `{operation.Surface}` ({operation.Availability}): read `{operation.ReadSource}` -> write `{operation.WriteTarget}`";
            yield return $"ServiceBundle diff `{operation.Surface}`: {operation.DiffSummary}";
        }
    }

    private static IEnumerable<string> BuildGitOpsPlanActions(GitOpsPlan plan)
    {
        yield return $"GitOps engine `{plan.Engine}` gate status: {plan.GateStatus}.";
        yield return $"GitOps diff summary: {plan.DiffSummary}.";
        yield return $"GitOps drift summary: {plan.DriftSummary}.";

        foreach (GitOpsEnvironmentPlan environment in plan.Environments)
        {
            yield return $"GitOps state `{environment.Environment}`: desired `{environment.DesiredRevision}`, actual `{environment.ActualRevision}`, diff `{environment.DiffStatus}`.";

            foreach (GitOpsDriftStatus drift in environment.Drift)
            {
                yield return $"GitOps drift `{environment.Environment}/{drift.Scope}`: {drift.Status}.";
            }

            foreach (GitOpsCommandPlan command in environment.Commands)
            {
                string approvalSuffix = command.RequiresApproval ? " (approval-gated)" : string.Empty;
                yield return $"GitOps command `{environment.Environment}/{command.Operation}`{approvalSuffix}: {command.Command}";
            }
        }

        foreach (GitOpsStateTransitionPlan transition in plan.StateTransitions)
        {
            yield return $"GitOps transition `{transition.Environment}/{transition.Operation}`: {transition.Summary}";
        }
    }

    private OperationEvidence BuildDeploymentEvidence(
        string terraformRepository,
        string terraformRef,
        IReadOnlyList<string> deploymentTargets,
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows,
        ReleaseOrchestrationPlan releaseOrchestration,
        ServiceBundleReconciliationPlan serviceBundleReconciliation,
        GitOpsPlan gitOpsPlan,
        DeploymentAuthorization authorization,
        BackendCallResult backendResult)
    {
        return new OperationEvidence(
            Scope: $"gitops-deploy:{string.Join("+", gitOpsPlan.Environments.Select(environment => environment.Environment))}",
            RequestedAction: gitOpsPlan.RequestedAction,
            EffectiveAction: gitOpsPlan.EffectiveAction,
            DryRun: authorization.DryRun,
            ExecutionMode: runtime.ExecutionMode.ToString().ToLowerInvariant(),
            ExecutionTier: runtime.ExecutionTier.ToConfigValue(),
            TargetEnvironments: gitOpsPlan.Environments.Select(environment => environment.Environment).ToArray(),
            CurrentRevision: GitOpsPlanner.BuildCurrentRevisionSummary(gitOpsPlan),
            DesiredRevision: gitOpsPlan.Environments.FirstOrDefault()?.DesiredRevision,
            GitOpsTool: gitOpsPlan.Engine,
            TerraformRepository: terraformRepository,
            TerraformRef: terraformRef,
            DeploymentTargets: deploymentTargets.ToArray(),
            PolicyGate: authorization.PolicyGate,
            ApprovalMode: EffectivePolicy.ApprovalMode.ToConfigValue(),
            AuditHookTarget: EffectivePolicy.AuditHookTarget,
            SupportSessionAccess: EffectivePolicy.SupportSession.Access.ToConfigValue(),
            SupportSessionTtlMinutes: EffectivePolicy.SupportSession.TtlMinutes,
            SupportSessionCustomerVisible: EffectivePolicy.SupportSession.CustomerVisible,
            BreakGlassPostActionReviewRequired: EffectivePolicy.BreakGlassPostActionReviewRequired,
            RequiredChecks: authorization.RequiredChecks
                .Concat(BuildOperatorPolicyValidationChecks())
                .Concat(gitOpsPlan.RequiredEvidence)
                .Concat(BuildRuntimeAdapterValidationChecks(adapterWorkflows))
                .Concat(ReleaseOrchestrationPlanner.FlattenEvidenceRequirements(releaseOrchestration))
                .Concat(ServiceBundleReconciliationPlanner.FlattenEvidenceRequirements(serviceBundleReconciliation))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DiffSummary: gitOpsPlan.DiffSummary,
            GateStatus: gitOpsPlan.GateStatus,
            BackendEndpoint: backendResult.Endpoint,
            BackendDetail: backendResult.Detail);
    }

    private OperationEvidence BuildUpgradeEvidence(
        string environment,
        string currentVersion,
        string targetVersion,
        ReleaseOrchestrationPlan releaseOrchestration,
        BackendCallResult backendResult)
    {
        bool dryRun = runtime.ExecutionMode != ExecutionMode.Execute;
        string[] requiredChecks = ReleaseOrchestrationPlanner.FlattenEvidenceRequirements(releaseOrchestration)
            .Concat(
            [
                "deploy-preflight",
                "post-upgrade-smoke",
                "rollback-readiness"
            ])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OperationEvidence(
            Scope: $"upgrade:{Normalize(environment, "unknown")}",
            RequestedAction: "upgrade",
            EffectiveAction: dryRun ? "plan-upgrade" : "upgrade",
            DryRun: dryRun,
            ExecutionMode: runtime.ExecutionMode.ToString().ToLowerInvariant(),
            ExecutionTier: runtime.ExecutionTier.ToConfigValue(),
            TargetEnvironments: [Normalize(environment, "unknown")],
            CurrentRevision: Normalize(currentVersion, "current"),
            DesiredRevision: Normalize(targetVersion, "target"),
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            DeploymentTargets: runtime.TerraformDeploymentTargets.ToArray(),
            PolicyGate: dryRun ? "plan-only-upgrade" : "staged-rollout-required",
            ApprovalMode: EffectivePolicy.ApprovalMode.ToConfigValue(),
            AuditHookTarget: EffectivePolicy.AuditHookTarget,
            SupportSessionAccess: EffectivePolicy.SupportSession.Access.ToConfigValue(),
            SupportSessionTtlMinutes: EffectivePolicy.SupportSession.TtlMinutes,
            SupportSessionCustomerVisible: EffectivePolicy.SupportSession.CustomerVisible,
            BreakGlassPostActionReviewRequired: EffectivePolicy.BreakGlassPostActionReviewRequired,
            RequiredChecks: requiredChecks,
            DiffSummary: $"{currentVersion}->{targetVersion}",
            GateStatus: dryRun ? "plan-only-upgrade" : "staged-rollout-required",
            BackendEndpoint: backendResult.Endpoint,
            BackendDetail: backendResult.Detail);
    }

    private sealed record DeploymentAuthorization(
        bool DryRun,
        string PolicyGate,
        IReadOnlyList<string> RequiredChecks,
        string WorkflowGuidance,
        string ValidationGuidance,
        string RiskGuidance);
}
