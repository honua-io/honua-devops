using System.ComponentModel;

using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.OrchestrationHost;
using Honua.DevOps.Agent.Operations.Troubleshooting;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

internal sealed class HonuaOperationsToolkit(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel? policy = null,
    SupportGateway? supportGateway = null,
    string? defaultEdition = null)
{
    private OperatorPolicyModel EffectivePolicy => policy ?? OperatorPolicyModel.Default;

    internal string SessionEdition => string.IsNullOrWhiteSpace(defaultEdition) ? "community" : defaultEdition!.Trim().ToLowerInvariant();

    [Description("Describe the connected Honua environment: readiness, edition and feature capabilities, manifest scope, deploy targets, and approved environments. Call this first whenever the operator's request lacks an explicit service, environment, or edition so subsequent tool calls are grounded in real state.")]
    public async Task<OperationResponse> DescribeEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        BackendCallResult readiness = await gateway.ProbeHonuaAsync(cancellationToken);
        using BackendJsonResult capabilities = await gateway.GetCapabilitySnapshotAsync(cancellationToken);
        using BackendJsonResult manifest = await gateway.ExportManifestSnapshotAsync(cancellationToken);

        string detectedEdition = TryReadEditionFromCapabilities(capabilities) ?? SessionEdition;

        List<string> findings =
        [
            $"Readiness probe: {readiness.Detail} ({readiness.Endpoint}).",
            $"Capabilities snapshot: {capabilities.CallResult.Detail} ({capabilities.CallResult.Endpoint}).",
            $"Manifest snapshot: {manifest.CallResult.Detail} ({manifest.CallResult.Endpoint}).",
            $"Detected edition: {detectedEdition} (session default: {SessionEdition}).",
            $"Allowed environments: {string.Join(", ", runtime.AllowedEnvironments)}.",
            $"GitOps tool: {runtime.GitOpsTool}.",
            $"Execution mode/tier: {runtime.ExecutionMode.ToString().ToLowerInvariant()}/{runtime.ExecutionTier.ToConfigValue()}.",
            $"Deploy target id: {(string.IsNullOrWhiteSpace(runtime.DeployTargetId) ? "unset" : runtime.DeployTargetId!)}.",
            $"Terraform local path: {(string.IsNullOrWhiteSpace(runtime.TerraformLocalPath) ? "unset" : runtime.TerraformLocalPath!)} (targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}).",
            $"Capabilities excerpt: {capabilities.CallResult.PayloadPreview}",
            $"Manifest excerpt: {manifest.CallResult.PayloadPreview}"
        ];

        bool allOk = readiness.IsSuccess && capabilities.CallResult.IsSuccess && manifest.CallResult.IsSuccess;

        return new OperationResponse(
            Status: allOk ? "environment-described" : "environment-degraded",
            Summary: $"Honua environment description (edition={detectedEdition}, ready={readiness.IsSuccess}).",
            Findings: findings,
            Actions:
            [
                "Use the detected edition when invoking edition-gated tools; do not guess.",
                "Reference allowed environments and deploy target id from this snapshot before scheduling deploys.",
                "If readiness or capabilities failed, fix configuration (HONUA_DEVOPS_HONUA_API_BASE_URL / HONUA_DEVOPS_HONUA_API_KEY) before any mutating call."
            ],
            ValidationChecks:
            [
                "Readiness probe returns success.",
                "Capabilities snapshot exposes an edition value.",
                "Manifest export returns a non-empty payload."
            ],
            Risks:
            [
                "Acting without grounding in this snapshot risks calling against the wrong edition or environment.",
                "Stale capability data may mask runtime drift; re-run describe before mutating operations."
            ],
            BackendSteps:
            [
                ToBackendStep("describe:readiness", readiness, mutatesState: false),
                ToBackendStep("describe:capabilities", capabilities.CallResult, mutatesState: false),
                ToBackendStep("describe:manifest", manifest.CallResult, mutatesState: false)
            ]);
    }

    private static string? TryReadEditionFromCapabilities(BackendJsonResult capabilities)
    {
        if (!capabilities.CallResult.IsSuccess || capabilities.Payload is null)
        {
            return null;
        }

        return BackendGateway.ExtractEditionFromCapabilities(capabilities.Payload);
    }

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
            runtime.DeployTargetId,
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
                $"Deploy-control target: {Normalize(runtime.DeployTargetId, "not configured")}.",
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
            ServiceBundleReconciliation: serviceBundleReconciliation,
            BackendSteps: BuildGitOpsBackendSteps(backendResult, authorization.DryRun));
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

    [Description("Plan the Azure-first Microsoft Agent Framework host path for analyze, publish, build, or deploy operator workflows.")]
    public Task<OperationResponse> PlanAzureOperatorWorkflowAsync(
        string workflowFamily,
        string environment,
        string operatorGoal,
        string packageReference,
        string deploymentTarget,
        bool publishExternally)
    {
        OperatorWorkflowFamily parsedFamily = OperatorWorkflowFamilyExtensions.Parse(workflowFamily);
        string defaultEnvironment = runtime.AllowedEnvironments.FirstOrDefault() ?? "dev";
        string normalizedEnvironment = ParseEnvironments(Normalize(environment, defaultEnvironment)).First();
        string normalizedGoal = SanitizeFreeText(operatorGoal, "operator workflow");
        string normalizedPackageReference = SanitizeFreeText(packageReference, string.Empty);
        string normalizedDeploymentTarget = SanitizeFreeText(deploymentTarget, string.Empty);

        OrchestrationHostPlan plan = AzureOrchestrationHostPlanner.Build(
            parsedFamily,
            normalizedEnvironment,
            normalizedGoal,
            normalizedPackageReference,
            normalizedDeploymentTarget,
            publishExternally,
            runtime,
            EffectivePolicy);

        List<string> findings =
        [
            $"Host target: {plan.HostTarget}.",
            $"Workflow family: {plan.WorkflowFamily.ToConfigValue()}.",
            $"Environment: {plan.Environment}.",
            $"Gate status: {plan.GateStatus}.",
            $"Contract surfaces: {string.Join(" | ", plan.ContractSurfaces)}"
        ];
        findings.AddRange(plan.AzureIntegrationPoints.Select(point => $"Azure integration: {point}"));

        List<string> actions = plan.Stages
            .Select(stage =>
                $"{stage.Stage.ToConfigValue()}: {stage.AzureHostResponsibility}")
            .ToList();

        List<string> validationChecks = plan.RequiredChecks
            .Concat(plan.EvaluationHooks)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> risks =
        [
            ..plan.BoundaryRules,
            "The host plan is a dry-run contract-consumption scaffold until concrete MCP and gRPC clients are wired."
        ];

        return Task.FromResult(new OperationResponse(
            Status: "orchestration-plan-ready",
            Summary: $"Azure operator workflow host plan for `{parsedFamily.ToConfigValue()}` in `{normalizedEnvironment}`.",
            Findings: findings,
            Actions: actions,
            ValidationChecks: validationChecks,
            Risks: risks,
            Evidence: BuildOrchestrationHostEvidence(plan),
            OrchestrationHost: plan));
    }

    [Description("Plan the full GitOps platform story: repo watching, promotion, drift alerting, CI/CD previews, rollback, and audit evidence.")]
    public Task<OperationResponse> PlanGitOpsPlatformAsync(
        string configRepository,
        string branch,
        string service,
        string environmentsCsv,
        string syncMode,
        string alertTargetsCsv,
        string commitSha,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedRepository = SanitizePayloadValue(configRepository, "config repository");
        string normalizedBranch = ValidateRevision(Normalize(branch, "main"), "branch");
        string normalizedService = ValidateServiceName(service);
        string[] targetEnvironments = ParseEnvironments(environmentsCsv);
        string normalizedSyncMode = Normalize(syncMode, "webhook").ToLowerInvariant() switch
        {
            "webhook" => "webhook",
            "polling" => "polling",
            "hybrid" => "hybrid",
            "webhook+polling" => "hybrid",
            _ => "webhook"
        };
        string[] alertTargets = SplitCsv(alertTargetsCsv, "slack");
        string normalizedCommitSha = ValidateRevision(Normalize(commitSha, "HEAD"), "commit SHA");
        string promotionPath = string.Join(" -> ", targetEnvironments);
        bool productionPromotion = targetEnvironments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        string gate = productionPromotion ? "promotion-approval" : "lower-env-validation";
        string[] supportedKinds =
        [
            "Connection",
            "ServiceBundle",
            "Layer",
            "Style",
            "ImportJob",
            "GeoProcessingPipeline",
            "EtlPipeline",
            "RoleDefinition",
            "RateLimitPolicy",
            "TenantConfig",
            "ExecutionPolicy",
            "Promotion"
        ];

        List<string> actions =
        [
            $"Configure `{normalizedRepository}` branch `{normalizedBranch}` as the desired-state source for `{normalizedService}`.",
            normalizedSyncMode == "polling"
                ? "Enable polling sync with durable last-seen commit tracking."
                : normalizedSyncMode == "hybrid"
                    ? "Enable webhook sync and retain polling as a missed-event backstop."
                    : "Enable webhook sync on merge to the watched branch.",
            $"Run `honua apply -f desired-state --dry-run --commit {normalizedCommitSha}` for PR previews before merge.",
            $"Promote through `{promotionPath}` with health checks, smoke tests, and manual approval before gated environments.",
            $"Publish drift alerts to {string.Join(", ", alertTargets)} and include visual diff evidence in the admin review path.",
            "Use rollback by commit SHA: `honua rollback --to <known-good-commit>`.",
            "Record apply, promote, rollback, and reconcile events to the audit trail with actor, commit, diff summary, and evidence links."
        ];
        actions.AddRange(BuildOperatorPolicyActions());

        return Task.FromResult(new OperationResponse(
            Status: "gitops-platform-ready",
            Summary: $"GitOps platform plan for `{normalizedService}` from `{normalizedRepository}` at `{normalizedBranch}`.",
            Findings:
            [
                $"Declarative resource kinds: {string.Join(", ", supportedKinds)}.",
                "Schema contract: versioned apiVersion/kind manifests with YAML or JSON input.",
                $"Repository watcher: {normalizedSyncMode}; deployed commit: {normalizedCommitSha}.",
                $"Promotion path: {promotionPath}; gate: {gate}.",
                "Drift model: runtime export compared with declared manifests, with optional remediation through approved sync.",
                "CI/CD integration: GitHub Actions and GitLab templates produce dry-run diffs, PR comments, and rollback evidence."
            ],
            Actions: actions,
            ValidationChecks:
            [
                "Manifest schema validation passes for every resource before apply.",
                "Dry-run diff is attached to the PR before merge.",
                "Commit SHA is recorded on every sync and shown in status output.",
                "Each promotion gate has health, smoke, and approval evidence.",
                "Drift alerts include target environment, declared commit, actual revision, and diff summary.",
                "Rollback drills can restore a known-good commit without manual manifest surgery."
            ],
            Risks:
            [
                "Webhook-only sync can miss events without replay or polling backstop.",
                "Environment overrides can hide drift if they are not part of the declared state contract.",
                "Auto-remediation must stay approval-gated for production drift."
            ],
            Evidence: BuildPlannerEvidence(
                $"gitops-platform:{normalizedService}",
                "plan-gitops-platform",
                targetEnvironments,
                normalizedCommitSha,
                "gitops-platform-contract",
                gate,
                $"repo={normalizedRepository}; branch={normalizedBranch}; sync={normalizedSyncMode}")));
    }

    [Description("Run edition-aware read-only diagnostics over Honua health, metrics, and error telemetry.")]
    public async Task<OperationResponse> HonuaDiagnoseAsync(
        string service,
        string environment,
        string timeframe,
        string symptoms,
        string edition,
        CancellationToken cancellationToken = default)
    {
        string normalizedService = ValidateServiceName(service);
        string normalizedEnvironment = Normalize(environment, "unknown");
        string normalizedSymptoms = SanitizeFreeText(symptoms, "health check requested");
        string normalizedEdition = NormalizeEdition(edition);
        BackendCallResult backendResult = await gateway.RequestTroubleshootAsync(
            normalizedService,
            normalizedEnvironment,
            normalizedSymptoms,
            "health-diagnostics",
            $"timeframe:{Normalize(timeframe, "recent window")}; edition:{normalizedEdition}",
            cancellationToken);

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "diagnosis-ready" : "backend-error",
            Summary: $"Health diagnosis for `{normalizedService}` in `{normalizedEnvironment}` ({normalizedEdition}).",
            Findings:
            [
                $"Honua API endpoint: {backendResult.Endpoint}",
                $"Backend result: {backendResult.Detail}",
                $"Response excerpt: {backendResult.PayloadPreview}",
                $"Symptoms: {normalizedSymptoms}.",
                "Community edition allows read-only health diagnostics; write-capable actions remain gated."
            ],
            Actions:
            [
                "Check readiness, error telemetry, and latency in the same incident window.",
                "Classify the likely failure domain before proposing remediation.",
                "Escalate to Pro or Enterprise tools only when diagnostics require tuning, migration, runbook, or remediation actions."
            ],
            ValidationChecks:
            [
                "Health endpoint returns a stable success response.",
                "Error rate and P95 latency are below the active SLO threshold."
            ],
            Risks:
            [
                "Read-only diagnostics can miss root cause when telemetry is incomplete.",
                "Symptoms without timeframe or affected route can produce broad findings."
            ],
            Evidence: BuildPlannerEvidence(
                $"ai-devops:diagnose:{normalizedService}",
                "honua_diagnose",
                [normalizedEnvironment],
                null,
                "read-only-diagnostics",
                "edition-community",
                backendResult.PayloadPreview,
                backendResult.Endpoint,
                backendResult.Detail));
    }

    [Description("Explain slow query signatures and identify spatial, cache, and pool bottlenecks.")]
    public async Task<OperationResponse> ExplainSlowQueriesAsync(
        string service,
        string environment,
        string timeframe,
        string slowQuerySample,
        string edition,
        CancellationToken cancellationToken = default)
    {
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "pro"))
        {
            return BuildEditionGateResponse("honua_explain_slow_queries", normalizedEdition, "pro");
        }

        string normalizedService = ValidateServiceName(service);
        string normalizedEnvironment = Normalize(environment, "unknown");
        string sample = SanitizeFreeText(slowQuerySample, "no sample provided");
        BackendCallResult backendResult = await gateway.QueryLogsAsync(
            normalizedService,
            normalizedEnvironment,
            timeframe,
            "slow-query-analysis",
            sample,
            cancellationToken);

        List<string> findings =
        [
            $"OTEL logs endpoint: {backendResult.Endpoint}",
            $"Backend result: {backendResult.Detail}",
            $"Response excerpt: {backendResult.PayloadPreview}"
        ];
        if (Contains(sample, "seq scan", "full scan"))
            findings.Add("Sequential scan indicators suggest missing attribute or spatial selectivity.");
        if (Contains(sample, "st_intersects", "bbox", "geometry"))
            findings.Add("Spatial predicate present; verify spatial index coverage and bounding-box prefiltering.");
        if (Contains(sample, "cache miss", "miss ratio"))
            findings.Add("Cache miss indicators suggest TTL, key cardinality, or seeding review.");

        return new OperationResponse(
            Status: backendResult.IsSuccess ? "slow-query-explained" : "backend-error",
            Summary: $"Slow query analysis for `{normalizedService}` in `{normalizedEnvironment}`.",
            Findings: findings,
            Actions:
            [
                "Compare slow query predicates against available spatial and attribute indexes.",
                "Add bounding-box prefilters before expensive geometry predicates when possible.",
                "Tune cache TTL and seeding only after query shape and index coverage are validated."
            ],
            ValidationChecks:
            [
                "Explain plan uses the expected spatial or compound index.",
                "P95 query latency improves under representative load."
            ],
            Risks:
            [
                "Index recommendations based on a single query can hurt write-heavy workloads.",
                "Cache tuning without query fixes can hide persistent database pressure."
            ]);
    }

    [Description("Recommend spatial and attribute indexes for a service layer with edition gating.")]
    public Task<OperationResponse> RecommendIndexesAsync(
        string service,
        string layer,
        string queryPattern,
        string currentIndexes,
        string edition,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "pro"))
        {
            return Task.FromResult(BuildEditionGateResponse("honua_recommend_indexes", normalizedEdition, "pro"));
        }

        string normalizedService = ValidateServiceName(service);
        string normalizedLayer = SanitizePayloadValue(layer, "layer");
        string pattern = SanitizeFreeText(queryPattern, "not provided");
        string indexes = SanitizeFreeText(currentIndexes, "none provided");
        List<string> recommendations =
        [
            Contains(pattern, "bbox", "intersects", "within", "geometry")
                ? "Add or verify spatial index coverage for the geometry column used by the primary map/filter predicate."
                : "Confirm whether the layer needs a spatial index before adding write-cost overhead.",
            Contains(pattern, "where", "tenant", "status", "category", "date")
                ? "Add a selective compound attribute index matching tenant/filter/sort order."
                : "Capture representative filters before adding attribute indexes.",
            "Measure write amplification and maintenance cost before promoting indexes to production."
        ];

        return Task.FromResult(new OperationResponse(
            Status: "index-plan-ready",
            Summary: $"Index recommendation plan for `{normalizedService}` layer `{normalizedLayer}`.",
            Findings:
            [
                $"Query pattern: {pattern}.",
                $"Current indexes: {indexes}.",
                $"Edition: {normalizedEdition}; required: pro."
            ],
            Actions: recommendations,
            ValidationChecks:
            [
                "Explain plan selects the expected index for the slow query sample.",
                "Index build completes in lower environment within the maintenance budget.",
                "P95 latency improves without unacceptable ingest/write regression."
            ],
            Risks:
            [
                "Low-cardinality indexes can increase planner noise without improving latency.",
                "Production index builds can compete with ingest and tile generation workloads."
            ]));
    }

    [Description("Forecast capacity from current traffic, growth, and utilization signals.")]
    public Task<OperationResponse> CapacityForecastAsync(
        string service,
        string environment,
        string metricWindow,
        double currentDailyRequests,
        double growthRatePercent,
        int currentNodes,
        double cpuUtilizationPercent,
        double memoryUtilizationPercent,
        string edition,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "pro"))
        {
            return Task.FromResult(BuildEditionGateResponse("honua_capacity_forecast", normalizedEdition, "pro"));
        }

        string normalizedService = ValidateServiceName(service);
        string normalizedEnvironment = Normalize(environment, "unknown");
        int safeNodes = Math.Max(1, currentNodes);
        double utilization = Math.Clamp(Math.Max(cpuUtilizationPercent, memoryUtilizationPercent) / 100.0, 0.01, 5.0);
        double growth = Math.Max(0.0, growthRatePercent) / 100.0;
        double projectedRequests30 = currentDailyRequests * Math.Pow(1 + growth, 30);
        double daysToScale = growth <= 0 || utilization >= 0.8
            ? 0
            : Math.Log(0.8 / utilization) / Math.Log(1 + growth);
        int recommendedNodes = utilization >= 0.7 || daysToScale <= 60
            ? safeNodes + 1
            : safeNodes;

        return Task.FromResult(new OperationResponse(
            Status: "capacity-forecast-ready",
            Summary: $"Capacity forecast for `{normalizedService}` in `{normalizedEnvironment}` over `{Normalize(metricWindow, "recent window")}`.",
            Findings:
            [
                $"Current daily requests: {currentDailyRequests:0}.",
                $"Projected daily requests in 30 days: {projectedRequests30:0}.",
                $"Current nodes: {safeNodes}; recommended nodes: {recommendedNodes}.",
                $"Peak utilization signal: {utilization:P0}; days until 80% pressure: {(daysToScale <= 0 ? "now" : daysToScale.ToString("0"))}."
            ],
            Actions:
            [
                recommendedNodes > safeNodes
                    ? $"Prepare scale-out from {safeNodes} to {recommendedNodes} node(s) before the next promotion gate."
                    : "Retain current node count and continue trend monitoring.",
                "Model cache, database, and ingest pressure separately before committing infrastructure spend.",
                "Use GitOps promotion to stage capacity changes before production rollout."
            ],
            ValidationChecks:
            [
                "Load test confirms headroom above 20% at projected 30-day volume.",
                "Cost delta is reviewed against traffic and SLO impact.",
                "Autoscaling thresholds align with observed burst windows."
            ],
            Risks:
            [
                "Linear growth assumptions can understate campaign or tenant onboarding bursts.",
                "CPU-only planning can miss database, cache, or storage bottlenecks."
            ]));
    }

    [Description("Prepare or execute approved operational runbooks with Enterprise and execution-tier gates.")]
    public async Task<OperationResponse> RunbookExecuteAsync(
        string runbookName,
        string service,
        string environment,
        string parameters,
        bool confirmed,
        string edition,
        CancellationToken cancellationToken = default)
    {
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "enterprise"))
        {
            return BuildEditionGateResponse("honua_runbook_execute", normalizedEdition, "enterprise");
        }

        string normalizedRunbook = SanitizePayloadValue(runbookName, "runbook");
        string normalizedService = ValidateServiceName(service);
        string normalizedEnvironment = Normalize(environment, "unknown");
        bool executionAllowed = confirmed &&
            runtime.ExecutionMode == ExecutionMode.Execute &&
            runtime.ExecutionTier is ExecutionTier.ExecuteLowerEnv or ExecutionTier.PromoteProd or ExecutionTier.BreakGlass;
        bool readOnlyRunbook = IsReadOnlyHonuaRunbook(normalizedRunbook);
        bool writeHonuaRunbook = IsWriteHonuaRunbook(normalizedRunbook);
        BackendCallResult? executionResult = null;
        string status = !confirmed
            ? "confirmation-required"
            : executionAllowed || readOnlyRunbook ? "runbook-execute-ready" : "runbook-plan-ready";

        if (confirmed && (readOnlyRunbook || (executionAllowed && writeHonuaRunbook)))
        {
            executionResult = await ExecuteHonuaRunbookAsync(
                normalizedRunbook,
                parameters,
                executionAllowed,
                cancellationToken);
            status = executionResult.IsSuccess ? "runbook-executed" : "backend-error";
        }

        List<string> findings =
        [
            $"Edition: {normalizedEdition}; required: enterprise.",
            $"Execution mode: {runtime.ExecutionMode}; tier: {runtime.ExecutionTier.ToConfigValue()}.",
            $"Confirmed: {confirmed}.",
            $"Parameters: {SanitizeFreeText(parameters, "none")}."
        ];
        if (executionResult is not null)
        {
            findings.Add($"Honua runbook endpoint: {executionResult.Endpoint}");
            findings.Add($"Honua runbook result: {executionResult.Detail}");
            findings.Add($"Honua runbook response: {executionResult.PayloadPreview}");
        }

        return new OperationResponse(
            Status: status,
            Summary: $"Runbook `{normalizedRunbook}` for `{normalizedService}` in `{normalizedEnvironment}`.",
            Findings: findings,
            Actions:
            [
                executionResult is not null
                    ? "Persist the runbook response with the incident or support ticket evidence."
                    : executionAllowed
                        ? "Execute the runbook through the approved operator path and capture command output."
                    : "Prepare the runbook plan only; do not perform write-capable steps.",
                "Require customer-visible approval, scoped credentials, and rollback intent before mutating resources.",
                "Attach validation evidence to the support ticket or incident record."
            ],
            ValidationChecks:
            [
                "Pre-checks pass before runbook execution.",
                "Post-checks prove the target service recovered or the change was rolled back.",
                "Audit event includes operator, approval, command scope, and TTL."
            ],
            Risks:
            [
                "Natural-language runbook requests can be ambiguous without named parameters.",
                "Write-capable runbooks must not bypass approval mode or support-session TTL."
            ],
            Evidence: BuildPlannerEvidence(
                $"ai-devops:runbook:{normalizedService}",
                normalizedRunbook,
                [normalizedEnvironment],
                null,
                "enterprise-runbook",
                status,
                SanitizeFreeText(parameters, "none")),
            BackendSteps: executionResult is null
                ? null
                : [ToBackendStep($"runbook:{normalizedRunbook}", executionResult, mutatesState: !readOnlyRunbook)]);
    }

    private async Task<BackendCallResult> ExecuteHonuaRunbookAsync(
        string normalizedRunbook,
        string parameters,
        bool writeExecutionAllowed,
        CancellationToken cancellationToken)
    {
        return normalizedRunbook.Trim().ToLowerInvariant() switch
        {
            "deploy-preflight" or "preflight" =>
                await gateway.RequestDeployPreflightAsync(includeDiagnostics: true, cancellationToken),
            "manifest-drift" or "drift" =>
                await gateway.RequestManifestDriftAsync(verbose: true, cancellationToken),
            "manifest-versions" or "manifest-history" =>
                await gateway.RequestManifestVersionsAsync(ParsePositiveIntParameter(parameters, "limit", 10), cancellationToken),
            "deploy-submit" when writeExecutionAllowed =>
                await gateway.SubmitDeployOperationAsync(
                    ExtractRequiredParameter(parameters, "operationId"),
                    SanitizeFreeText(parameters, "approved runbook submit"),
                    cancellationToken),
            "deploy-rollback" or "rollback" when writeExecutionAllowed =>
                await gateway.RollbackDeployOperationAsync(
                    ExtractRequiredParameter(parameters, "operationId"),
                    SanitizeFreeText(parameters, "approved runbook rollback"),
                    cancellationToken),
            _ => new BackendCallResult(
                IsSuccess: false,
                Endpoint: "local://honua-devops/runbook-router",
                Detail: "unsupported-runbook",
                PayloadPreview: "Supported runbooks: deploy-preflight, manifest-drift, manifest-versions, deploy-submit, deploy-rollback.")
        };
    }

    private static bool IsReadOnlyHonuaRunbook(string runbookName)
    {
        return runbookName.Trim().ToLowerInvariant() is
            "deploy-preflight" or
            "preflight" or
            "manifest-drift" or
            "drift" or
            "manifest-versions" or
            "manifest-history";
    }

    private static bool IsWriteHonuaRunbook(string runbookName)
    {
        return runbookName.Trim().ToLowerInvariant() is
            "deploy-submit" or
            "deploy-rollback" or
            "rollback";
    }

    private static string ExtractRequiredParameter(string parameters, string key)
    {
        string? value = TryExtractParameter(parameters, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Runbook parameter `{key}` is required.");
        }

        return value;
    }

    private static int ParsePositiveIntParameter(string parameters, string key, int fallback)
    {
        string? value = TryExtractParameter(parameters, key);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string? TryExtractParameter(string parameters, string key)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return null;
        }

        string[] tokens = parameters.Split(
            [' ', '\n', '\r', '\t', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string token in tokens)
        {
            int separatorIndex = token.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
            {
                continue;
            }

            string tokenKey = token[..separatorIndex].Trim().Trim('"', '\'');
            if (!tokenKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                !tokenKey.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Equals(key.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return token[(separatorIndex + 1)..].Trim().Trim('"', '\'');
        }

        return null;
    }

    [Description("Generate an incident summary with timeline, impact, response actions, and closure checks.")]
    public Task<OperationResponse> IncidentSummaryAsync(
        string service,
        string environment,
        string timeRange,
        string timelineEvents,
        string affectedServices,
        string edition,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "enterprise"))
        {
            return Task.FromResult(BuildEditionGateResponse("honua_incident_summary", normalizedEdition, "enterprise"));
        }

        string normalizedService = ValidateServiceName(service);
        string normalizedEnvironment = Normalize(environment, "unknown");
        string[] affected = SplitCsv(affectedServices, normalizedService);

        return Task.FromResult(new OperationResponse(
            Status: "incident-summary-ready",
            Summary: $"Incident summary for `{normalizedService}` in `{normalizedEnvironment}` during `{Normalize(timeRange, "recent window")}`.",
            Findings:
            [
                $"Affected services: {string.Join(", ", affected)}.",
                $"Timeline: {SanitizeFreeText(timelineEvents, "timeline not provided")}.",
                "Impact should be stated in customer-visible terms and linked to SLO/error-budget evidence."
            ],
            Actions:
            [
                "Record start, detect, mitigate, recover, and close timestamps.",
                "List root cause separately from contributing factors.",
                "Create follow-up items for prevention, detection, and runbook updates."
            ],
            ValidationChecks:
            [
                "Affected services have recovered and alerts are stable.",
                "Customer-visible impact and remediation summary are reviewed.",
                "Follow-up owners and due dates are assigned."
            ],
            Risks:
            [
                "A timeline without evidence links can turn into speculation.",
                "Closing before recovery verification weakens post-incident learning."
            ]));
    }

    [Description("Analyze a source GIS deployment and generate a migration plan with risk scoring.")]
    public Task<OperationResponse> MigrationAdvisorAsync(
        string sourcePlatform,
        string serviceInventory,
        string dataVolumeSummary,
        string protocolRequirements,
        string migrationConstraints,
        string edition,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "pro"))
        {
            return Task.FromResult(BuildEditionGateResponse("honua_migration_advisor", normalizedEdition, "pro"));
        }

        string source = SanitizeFreeText(sourcePlatform, "Esri ArcGIS Enterprise");
        string inventory = SanitizeFreeText(serviceInventory, "inventory not provided");
        string protocols = SanitizeFreeText(protocolRequirements, "OGC API, tiles, feature access");
        bool highRisk = Contains(inventory, "custom extension", "geoprocessing", "network analyst") ||
            Contains(migrationConstraints, "zero downtime", "regulated", "manual");

        return Task.FromResult(new OperationResponse(
            Status: "migration-plan-ready",
            Summary: $"Migration advisor plan from `{source}`.",
            Findings:
            [
                $"Service inventory: {inventory}.",
                $"Data volume: {SanitizeFreeText(dataVolumeSummary, "unknown")}.",
                $"Protocol requirements: {protocols}.",
                $"Risk band: {(highRisk ? "elevated" : "standard")}."
            ],
            Actions:
            [
                "Group services into clean protocol matches, transform-required services, and manual-review services.",
                "Stage migration by read-only publish, validation, dual-run, cutover, and rollback checkpoint.",
                "Track completion percentage by service, layer, data volume, and protocol parity."
            ],
            ValidationChecks:
            [
                "Inventory includes service/layer count, data volume, auth mode, and protocol usage.",
                "Representative map, feature, and tile requests match expected responses.",
                "Rollback plan preserves source availability until cutover is accepted."
            ],
            Risks:
            [
                highRisk
                    ? "Custom geoprocessing or strict cutover constraints need manual migration design."
                    : "Uncataloged clients can still break if protocol behavior differs.",
                "Data-volume estimates without sample migration timings can understate cutover duration."
            ]));
    }

    [Description("Plan Enterprise-gated self-healing actions with policy, approval, rollback, and validation controls.")]
    public async Task<OperationResponse> AutoRemediationPlanAsync(
        string service,
        string environment,
        string detectedIssue,
        string desiredOutcome,
        bool autoApply,
        string edition,
        CancellationToken cancellationToken = default)
    {
        string normalizedEdition = NormalizeEdition(edition);
        if (!EditionAtLeast(normalizedEdition, "enterprise"))
        {
            return BuildEditionGateResponse("honua_auto_remediation_plan", normalizedEdition, "enterprise");
        }

        string normalizedService = ValidateServiceName(service);
        string normalizedEnvironment = Normalize(environment, "unknown");
        bool canApply = autoApply &&
            runtime.ExecutionMode == ExecutionMode.Execute &&
            EffectivePolicy.ApprovalMode == ApprovalMode.DirectAllowed &&
            runtime.ExecutionTier is ExecutionTier.ExecuteLowerEnv or ExecutionTier.BreakGlass;
        string status = canApply ? "auto-remediation-ready" : "auto-remediation-approval-required";
        string sanitizedIssue = SanitizeFreeText(detectedIssue, "not provided");
        string sanitizedOutcome = SanitizeFreeText(desiredOutcome, "restore service health");
        string? operationId = TryExtractParameter($"{detectedIssue} {desiredOutcome}", "operationId") ??
                              TryExtractParameter($"{detectedIssue} {desiredOutcome}", "operation-id");
        BackendCallResult? remediationResult = null;

        if (canApply)
        {
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                remediationResult = await gateway.RollbackDeployOperationAsync(
                    operationId,
                    $"auto-remediation:{normalizedService}:{sanitizedIssue}",
                    cancellationToken);
                status = remediationResult.IsSuccess ? "auto-remediation-applied" : "backend-error";
            }
            else if (Contains(sanitizedIssue, "drift") || Contains(sanitizedOutcome, "drift"))
            {
                remediationResult = await gateway.RequestManifestDriftAsync(verbose: true, cancellationToken);
                status = remediationResult.IsSuccess ? "auto-remediation-observed" : "backend-error";
            }
            else
            {
                status = "auto-remediation-ready";
            }
        }

        List<string> findings =
        [
            $"Detected issue: {sanitizedIssue}.",
            $"Desired outcome: {sanitizedOutcome}.",
            $"Auto-apply requested: {autoApply}; resolved status: {status}.",
            $"Approval mode: {EffectivePolicy.ApprovalMode.ToConfigValue()}."
        ];
        if (remediationResult is not null)
        {
            findings.Add($"Honua remediation endpoint: {remediationResult.Endpoint}");
            findings.Add($"Honua remediation result: {remediationResult.Detail}");
            findings.Add($"Honua remediation response: {remediationResult.PayloadPreview}");
        }

        return new OperationResponse(
            Status: status,
            Summary: $"Auto-remediation plan for `{normalizedService}` in `{normalizedEnvironment}`.",
            Findings: findings,
            Actions:
            [
                remediationResult is not null
                    ? "Record the Honua remediation response and keep observing health until the issue signature clears."
                    : canApply
                    ? "Apply the narrow remediation through the operator execution path and capture rollback evidence."
                    : "Generate remediation proposal only; require approval before mutation.",
                "Prefer reversible actions: restart, scale, cache clear, feature flag rollback, or GitOps rollback.",
                "Stop automation if validation does not improve health within the observation window."
            ],
            ValidationChecks:
            [
                "Pre-action evidence proves the issue signature.",
                "Post-action health, latency, and error-rate checks pass.",
                "Rollback path is prepared and tested before any production mutation."
            ],
            Risks:
            [
                "False-positive detection can make automation worse than the original issue.",
                "Auto-remediation in production must remain tied to explicit policy and audit evidence."
            ],
            Evidence: BuildPlannerEvidence(
                $"ai-devops:auto-remediation:{normalizedService}",
                "honua_auto_remediation_plan",
                [normalizedEnvironment],
                null,
                "enterprise-auto-remediation",
                status,
                sanitizedIssue),
            BackendSteps: remediationResult is null
                ? null
                : [ToBackendStep("auto-remediation", remediationResult, mutatesState: !string.IsNullOrWhiteSpace(operationId))]);
    }

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

    private OperationEvidence BuildOrchestrationHostEvidence(OrchestrationHostPlan plan)
    {
        return new OperationEvidence(
            Scope: $"azure-orchestration-host:{plan.WorkflowFamily.ToConfigValue()}:{plan.Environment}",
            RequestedAction: plan.OperatorGoal,
            EffectiveAction: "plan-azure-operator-workflow",
            DryRun: true,
            ExecutionMode: runtime.ExecutionMode.ToString().ToLowerInvariant(),
            ExecutionTier: runtime.ExecutionTier.ToConfigValue(),
            TargetEnvironments: [plan.Environment],
            CurrentRevision: null,
            DesiredRevision: plan.PackageReference,
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            DeploymentTargets: runtime.TerraformDeploymentTargets.ToArray(),
            PolicyGate: plan.GateStatus,
            ApprovalMode: EffectivePolicy.ApprovalMode.ToConfigValue(),
            AuditHookTarget: EffectivePolicy.AuditHookTarget,
            SupportSessionAccess: EffectivePolicy.SupportSession.Access.ToConfigValue(),
            SupportSessionTtlMinutes: EffectivePolicy.SupportSession.TtlMinutes,
            SupportSessionCustomerVisible: EffectivePolicy.SupportSession.CustomerVisible,
            BreakGlassPostActionReviewRequired: EffectivePolicy.BreakGlassPostActionReviewRequired,
            RequiredChecks: plan.RequiredChecks,
            DiffSummary: $"orchestration stages: {string.Join(" -> ", plan.Stages.Select(stage => stage.Stage.ToConfigValue()))}",
            GateStatus: plan.GateStatus,
            BackendEndpoint: "local://azure-orchestration-host",
            BackendDetail: "contract-consumption plan only; no backend call performed");
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
                    autoBundleResult = await supportGateway.TriggerAutoBundleAsync(
                        ticketId,
                        instanceUrl,
                        gateway.Configuration.HonuaApiKey,
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
                BackendCallResult postResult = await supportGateway.PostDiagnosisAsync(
                    ticketId,
                    guidedFix,
                    evidence,
                    scorecard,
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

    private OperationResponse BuildEditionGateResponse(string toolName, string currentEdition, string requiredEdition)
    {
        return new OperationResponse(
            Status: "edition-gated",
            Summary: $"Tool `{toolName}` requires `{requiredEdition}` edition; current edition is `{currentEdition}`.",
            Findings:
            [
                $"Current edition: {currentEdition}.",
                $"Required edition: {requiredEdition}.",
                "Community is limited to read-only health diagnostics; Pro adds troubleshooting, tuning, capacity, and migration planning; Enterprise adds runbook execution, incident response, and auto-remediation."
            ],
            Actions:
            [
                $"Run this workflow in `{requiredEdition}` edition or use a lower-tier read-only diagnostic tool.",
                "Keep any generated plan read-only until edition and approval gates are satisfied."
            ],
            ValidationChecks:
            [
                "Edition is recorded in the operation evidence.",
                "Write-capable workflows have explicit approval and audit context."
            ],
            Risks:
            [
                "Bypassing edition gates can expose unsupported or unsafe operational actions."
            ]);
    }

    private OperationEvidence BuildPlannerEvidence(
        string scope,
        string requestedAction,
        IReadOnlyList<string> targetEnvironments,
        string? desiredRevision,
        string policyGate,
        string gateStatus,
        string? diffSummary,
        string backendEndpoint = "local-planner",
        string backendDetail = "not-sent")
    {
        bool writeReady = runtime.ExecutionMode == ExecutionMode.Execute &&
            gateStatus.Contains("ready", StringComparison.OrdinalIgnoreCase) &&
            !gateStatus.Contains("approval-required", StringComparison.OrdinalIgnoreCase) &&
            !gateStatus.Contains("confirmation-required", StringComparison.OrdinalIgnoreCase) &&
            !gateStatus.Contains("plan", StringComparison.OrdinalIgnoreCase);

        return new OperationEvidence(
            Scope: scope,
            RequestedAction: requestedAction,
            EffectiveAction: writeReady ? requestedAction : "plan-only",
            DryRun: !writeReady,
            ExecutionMode: runtime.ExecutionMode.ToString().ToLowerInvariant(),
            ExecutionTier: runtime.ExecutionTier.ToConfigValue(),
            TargetEnvironments: targetEnvironments,
            CurrentRevision: null,
            DesiredRevision: desiredRevision,
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            DeploymentTargets: runtime.TerraformDeploymentTargets,
            PolicyGate: policyGate,
            ApprovalMode: EffectivePolicy.ApprovalMode.ToConfigValue(),
            AuditHookTarget: EffectivePolicy.AuditHookTarget,
            SupportSessionAccess: EffectivePolicy.SupportSession.Access.ToConfigValue(),
            SupportSessionTtlMinutes: EffectivePolicy.SupportSession.TtlMinutes,
            SupportSessionCustomerVisible: EffectivePolicy.SupportSession.CustomerVisible,
            BreakGlassPostActionReviewRequired: EffectivePolicy.BreakGlassPostActionReviewRequired,
            RequiredChecks:
            [
                "edition-gate",
                "approval-context",
                "audit-evidence"
            ],
            DiffSummary: diffSummary,
            GateStatus: gateStatus,
            BackendEndpoint: backendEndpoint,
            BackendDetail: backendDetail);
    }

    private static string[] SplitCsv(string value, string fallback)
    {
        string[] items = Normalize(value, fallback)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items.Length == 0 ? [fallback] : items;
    }

    private string NormalizeEdition(string edition)
    {
        string fallback = SessionEdition;
        string resolved = Normalize(edition, fallback).ToLowerInvariant();
        return resolved switch
        {
            "community" => "community",
            "pro" => "pro",
            "professional" => "pro",
            "enterprise" => "enterprise",
            _ => fallback
        };
    }

    private bool EditionAtLeast(string currentEdition, string requiredEdition)
    {
        return EditionRank(currentEdition) >= EditionRank(requiredEdition);
    }

    private int EditionRank(string edition)
    {
        return NormalizeEdition(edition) switch
        {
            "enterprise" => 3,
            "pro" => 2,
            _ => 1
        };
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
            string mutation = transition.MutatesState ? "mutating" : "read-only";
            string approval = transition.RequiresApproval ? "approval-required" : "no-approval";
            yield return $"GitOps transition `{transition.Environment}/{transition.Operation}`: {transition.FromState} -> {transition.ToState} ({mutation}, {approval}). {transition.Summary}";
        }
    }

    private static IReadOnlyList<OperationBackendStep> BuildGitOpsBackendSteps(
        GitOpsDeployBackendResult backendResult,
        bool dryRun)
    {
        List<OperationBackendStep> steps =
        [
            ToBackendStep("manifest-export", backendResult.ExportResult, mutatesState: false),
            ToBackendStep("capabilities", backendResult.CapabilitiesResult, mutatesState: false),
            ToBackendStep("manifest-apply", backendResult.ApplyResult, mutatesState: !dryRun)
        ];

        if (backendResult.DeployPreflightResult is not null)
        {
            steps.Add(ToBackendStep("deploy-preflight", backendResult.DeployPreflightResult, mutatesState: false));
        }

        if (backendResult.DeployPlanResult is not null)
        {
            steps.Add(ToBackendStep("deploy-plan", backendResult.DeployPlanResult, mutatesState: false));
        }

        if (backendResult.DeployOperationResult is not null)
        {
            steps.Add(ToBackendStep("deploy-operation", backendResult.DeployOperationResult, mutatesState: !dryRun));
        }

        if (backendResult.DeployOperationStatusResult is not null)
        {
            steps.Add(ToBackendStep("deploy-operation-status", backendResult.DeployOperationStatusResult, mutatesState: false));
        }

        if (backendResult.ManifestDriftResult is not null)
        {
            steps.Add(ToBackendStep("manifest-drift", backendResult.ManifestDriftResult, mutatesState: false));
        }

        return steps;
    }

    private static OperationBackendStep ToBackendStep(string name, BackendCallResult result, bool mutatesState)
    {
        return new OperationBackendStep(
            Name: name,
            Endpoint: result.Endpoint,
            Success: result.IsSuccess,
            Detail: result.Detail,
            PayloadPreview: result.PayloadPreview,
            MutatesState: mutatesState);
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
