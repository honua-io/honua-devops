using System.ComponentModel;

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

internal sealed class HonuaOperationsToolkit(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel? policy = null,
    SupportGateway? supportGateway = null,
    string? defaultEdition = null)
{
    private OperatorPolicyModel EffectivePolicy => policy ?? OperatorPolicyModel.Default;

    internal string SessionEdition => string.IsNullOrWhiteSpace(defaultEdition) ? "community" : defaultEdition!.Trim().ToLowerInvariant();

    [Description("Search the operator's audit journal for recent operations. Returns operationId, timestamp, tool, status, summary, mutated flag, and execution tier. Use this to look up an operationId for rollback, recall what was run in a prior session, or summarize recent activity. Filter by tool name (exact match), mutatedOnly (true to skip read-only calls), or statusContains (substring match). Returns up to `limit` most-recent matches.")]
    public Task<OperationSearchResult> FindRecentOperationsAsync(
        string toolFilter,
        bool mutatedOnly,
        string statusContains,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        int safeLimit = limit < 1 ? 20 : Math.Min(limit, 200);
        bool? mutatedToggle = mutatedOnly ? true : null;
        OperationSearchResult result = OperationJournal.Find(
            EffectivePolicy.AuditHookTarget,
            string.IsNullOrWhiteSpace(toolFilter) ? null : toolFilter,
            mutatedToggle,
            string.IsNullOrWhiteSpace(statusContains) ? null : statusContains,
            safeLimit);
        return Task.FromResult(result);
    }

    [Description("Describe the connected Honua environment: readiness, edition and feature capabilities, manifest scope, deploy targets, and approved environments. Call this first whenever the operator's request lacks an explicit service, environment, or edition so subsequent tool calls are grounded in real state.")]
    public async Task<OperationResponse> DescribeEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        Task<BackendCallResult> readinessTask = gateway.ProbeHonuaAsync(cancellationToken);
        Task<BackendJsonResult> capabilitiesTask = gateway.GetCapabilitySnapshotAsync(cancellationToken);
        Task<BackendJsonResult> manifestTask = gateway.ExportManifestSnapshotAsync(cancellationToken);
        await Task.WhenAll(readinessTask, capabilitiesTask, manifestTask);
        BackendCallResult readiness = readinessTask.Result;
        using BackendJsonResult capabilities = capabilitiesTask.Result;
        using BackendJsonResult manifest = manifestTask.Result;

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
        OperationResponseBuilder builder = new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "analysis-ready" : "backend-error")
            .Summary($"Log analysis request for {scope}.")
            .WithBackend(backendResult, "OTEL logs");

        if (Contains(logSample, "timeout", "timed out"))
        {
            builder.AddFinding("Timeout indicators present in provided sample.");
        }

        if (Contains(logSample, "connection", "pool"))
        {
            builder.AddFinding("Connection or pool pressure indicators present in provided sample.");
        }

        if (!backendResult.IsSuccess)
        {
            builder.AddFinding("Live log query failed. Validate OTEL endpoint path, auth key, and query payload contract.");
        }

        return builder
            .AddActions([
                "Correlate errors by trace id and isolate first-failure boundary.",
                "Compare failing routes against slowest queries in the same window.",
                "Apply smallest corrective change and re-check SLOs."
            ])
            .AddValidationChecks([
                "Error rate returns below baseline for at least one alert window.",
                "P95 latency and retry volume trend downward after mitigation."
            ])
            .AddRisks([
                "Incomplete log context can hide upstream root cause.",
                "Treating symptom-only signatures can cause recurrence."
            ])
            .Build();
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
        OperationResponseBuilder builder = new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "analysis-ready" : "backend-error")
            .Summary($"Metric analysis request for {scope}.")
            .WithBackend(backendResult, "OTEL metrics")
            .AddFinding($"Optimization objective: {Normalize(objective, "improve latency and stability")}.");

        if (!backendResult.IsSuccess)
        {
            builder.AddFinding("Live metric query failed. Verify OTEL metrics path and authentication configuration.");
        }

        return builder
            .AddActions([
                "Rank bottlenecks by user impact, not by raw utilization alone.",
                "Apply one tuning change at a time and capture before/after metrics.",
                "Promote validated tuning from dev -> staging -> prod via GitOps."
            ])
            .AddValidationChecks([
                "SLO indicators improve without error-rate regression.",
                "Resource headroom remains above safety threshold."
            ])
            .AddRisks([
                "Burst windows can skew optimization decisions.",
                "Aggressive tuning can reduce failover resiliency."
            ])
            .Build();
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

        return new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "plan-ready" : "backend-error")
            .Summary($"Performance tuning request for service `{service}` in `{environment}`.")
            .WithBackend(backendResult, "Honua API")
            .AddFinding($"Target SLO: {Normalize(targetSlo, "stabilize P95 latency and error budget")}.")
            .AddActions([
                "Tune data path first: query shape, index coverage, and filtering strategy.",
                "Tune runtime next: connection pool, cache behavior, and timeout policy.",
                "Roll out tuning with canary checks before broad promotion."
            ])
            .AddValidationChecks([
                "P95/P99 latency improves under representative load.",
                "Throughput improves without saturation alarms."
            ])
            .AddRisks([
                "Over-indexing can increase write and maintenance costs.",
                "Cache-only tuning can hide underlying query inefficiency."
            ])
            .Build();
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

        return new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "triage-ready" : "backend-error")
            .Summary($"Incident triage request for `{service}` in `{environment}`.")
            .WithBackend(backendResult, "Honua API")
            .AddFinding($"Business impact: {Normalize(businessImpact, "unknown")}.")
            .AddActions([
                "Stabilize traffic and reduce blast radius before deep changes.",
                "Correlate deployment diff, logs, and metrics in the same incident window.",
                "Apply narrow corrective action and validate recovery before closure."
            ])
            .AddValidationChecks([
                "User impact is reduced or eliminated.",
                "No new high-severity alerts in full observation window."
            ])
            .AddRisks([
                "Parallel uncoordinated mitigations can obscure root cause.",
                "Skipping rollback criteria can extend outage duration."
            ])
            .Build();
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
            backendResult.ExportResult.PayloadPreview,
            adapterWorkflows,
            normalizedRevision);
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

        // Phase 1 actuation spine: for the mutating actions (sync/apply/promote) actuate
        // THROUGH the server deploy-control endpoints via the GitOps executors. The executor
        // re-derives the safety decision from EXECUTION_MODE + approval mode + the per-request
        // dry-run verdict, so with the default plan posture this returns plan-only and touches
        // nothing. Read-only actions (plan/dry-run) and dryRun authorizations skip actuation.
        GitOpsExecutionResult? execution = await MaybeActuateGitOpsAsync(
            normalizedService,
            targetEnvironments,
            normalizedRevision,
            normalizedAction,
            normalizedChangeSummary,
            authorization,
            cancellationToken);

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

        List<string> deployFindings =
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
        ];
        if (execution is not null)
        {
            deployFindings.Add($"GitOps actuation status: {execution.Status} (mutated={execution.Mutated}).");
            deployFindings.AddRange(execution.Findings.Select(finding => $"Actuation: {finding}"));
        }

        // The response Status reflects the actuation outcome when one ran; otherwise it keeps
        // the prior plan-only / execute-enabled / backend-error contract.
        string responseStatus = !backendResult.CombinedResult.IsSuccess
            ? "backend-error"
            : execution is not null
                ? MapActuationStatus(execution.Status)
                : runtime.ExecutionMode == ExecutionMode.Execute ? "execute-enabled" : "plan-only";

        IReadOnlyList<OperationBackendStep> backendSteps = BuildGitOpsBackendSteps(backendResult, authorization.DryRun);
        if (execution is not null && execution.BackendSteps.Count > 0)
        {
            backendSteps = [.. backendSteps, .. execution.BackendSteps];
        }

        return new OperationResponse(
            Status: responseStatus,
            Summary: $"GitOps deployment plan for `{normalizedService}` across {string.Join(", ", targetEnvironments)}.",
            Findings: deployFindings,
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
            BackendSteps: backendSteps);
    }

    // Decide whether this deploy request should actuate through the deploy-control executors,
    // and run the matching executor when it should. Returns null when no actuation applies
    // (read-only actions, or actions that are not yet wired to an executor); a non-null result
    // always carries the actuation status + backend steps to fold into the response. The
    // executors themselves stay default-safe: with EXECUTION_MODE=plan they return plan-only
    // and never touch the backend, so this is a no-op mutation-wise in the default posture.
    private async Task<GitOpsExecutionResult?> MaybeActuateGitOpsAsync(
        string service,
        IReadOnlyList<string> environments,
        string revision,
        string action,
        string reason,
        DeploymentAuthorization authorization,
        CancellationToken cancellationToken)
    {
        // Only sync/apply and promote create-and-submit through the executor here. Rollback is
        // surfaced through the dedicated RollbackGitOpsOperationAsync tool because it operates
        // on an existing operationId. plan/dry-run never actuate.
        bool isPromote = action.Equals("promote", StringComparison.OrdinalIgnoreCase);
        bool isSync = action is "sync" or "apply";
        if (!isPromote && !isSync)
        {
            return null;
        }

        string idempotencyKey = ConsoleOperationBridge.BuildProposalIdempotencyKey(
            Normalize(runtime.DeployTargetId, "unconfigured"),
            service,
            environments,
            revision,
            action);
        string correlationId = $"honua-devops:{action}:{service}";
        bool targetsProd = environments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        string priority = targetsProd ? "high" : "normal";
        Dictionary<string, string> parameters = new(StringComparer.Ordinal)
        {
            ["service"] = service,
            ["environments"] = string.Join(",", environments),
            ["action"] = action,
            ["source"] = $"honua-devops:{action}"
        };

        if (isPromote)
        {
            PromotionExecutor promotionExecutor = new(runtime, gateway, EffectivePolicy);
            return await promotionExecutor.ExecutePromotionAsync(
                revision,
                currentRevision: null,
                reason,
                idempotencyKey,
                correlationId,
                priority,
                parameters,
                authorization.DryRun,
                authorization.PolicyGate,
                cancellationToken);
        }

        GitOpsExecutor executor = new(runtime, gateway, EffectivePolicy);
        return await executor.ExecuteSyncAsync(
            revision,
            currentRevision: null,
            reason,
            idempotencyKey,
            correlationId,
            priority,
            parameters,
            authorization.DryRun,
            authorization.PolicyGate,
            cancellationToken);
    }

    // Maps the executor's actuation status onto the tool's response Status vocabulary.
    private static string MapActuationStatus(string actuationStatus)
        => actuationStatus switch
        {
            GitOpsExecutionStatus.PlanOnly => "plan-only",
            GitOpsExecutionStatus.AwaitingApproval => "awaiting-approval",
            GitOpsExecutionStatus.Succeeded => "execute-succeeded",
            GitOpsExecutionStatus.RolledBack => "rolled-back",
            GitOpsExecutionStatus.Failed => "execute-failed",
            GitOpsExecutionStatus.ApprovalRequired => "approval-required",
            GitOpsExecutionStatus.ContractUnavailable => "contract-unavailable",
            _ => "execute-enabled"
        };

    [Description("Roll back a durable honua-server deploy-control operation to its prior known-good revision by operationId. Safety-gated: with EXECUTION_MODE=plan (default) nothing is issued; a data-affecting rollback (rollbackPlan.IsDataAffecting / non-MetadataOnly class, or an unknown classification) ALWAYS requires explicit governed approval and is refused here rather than auto-issued; only a non-data-affecting rollback under a direct-allowed/break-glass approval mode is issued, and even then the server's OperatorApprovalGate is honored (a 403 surfaces as approval-required). Emits backend steps and the rollback classification as evidence.")]
    public async Task<OperationResponse> RollbackGitOpsOperationAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        string normalizedOperationId = DeploymentInputs.Normalize(operationId, string.Empty);
        if (normalizedOperationId.Length is < 1 or > 200 ||
            normalizedOperationId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("Operation id must be 1-200 characters with no whitespace or control characters.");
        }

        string normalizedReason = SanitizeFreeText(reason, "not provided");
        // Rollback authorization mirrors a destructive action: never treated as plan/dry-run by
        // request shape; the executor folds EXECUTION_MODE + approval mode + tier into its decision.
        DeploymentAuthorization authorization = AuthorizeDeployment(["prod"], "rollback");

        RollbackExecutor rollbackExecutor = new(runtime, gateway, EffectivePolicy);
        GitOpsExecutionResult execution = await rollbackExecutor.ExecuteRollbackAsync(
            normalizedOperationId,
            normalizedReason,
            authorization.DryRun,
            authorization.PolicyGate,
            cancellationToken);

        List<string> findings =
        [
            $"Rollback target operation: {normalizedOperationId}.",
            $"Actuation status: {execution.Status} (mutated={execution.Mutated}).",
            ..execution.Findings,
            $"Approval mode: {EffectivePolicy.ApprovalMode.ToConfigValue()}.",
            $"Audit hook target: {EffectivePolicy.AuditHookTarget}."
        ];

        return new OperationResponse(
            Status: MapActuationStatus(execution.Status),
            Summary: $"GitOps rollback for deploy-control operation `{normalizedOperationId}` ({execution.Status}).",
            Findings: findings,
            Actions:
            [
                "Rollback actuates through the deploy-control rollback endpoint; the agent never bypasses the OperatorApprovalGate.",
                execution.Status == GitOpsExecutionStatus.ApprovalRequired
                    ? "Surface the operationId and rollback classification for governed approval before retrying."
                    : "Verify post-rollback health and capture rollback evidence.",
                .. execution.BlockingReasons.Select(reasonText => $"Blocking: {reasonText}.")
            ],
            ValidationChecks:
            [
                "rollback-actuates-through-deploy-control",
                "data-affecting-rollback-requires-explicit-approval",
                "plan-mode-issues-no-rollback",
                "operator-approval-gate-never-bypassed"
            ],
            Risks:
            [
                "A data-affecting rollback can lose or rewrite data; only governed, evidenced approval should authorize it.",
                "Rolling back after long reconcile windows can diverge from the last known-good revision."
            ],
            BackendSteps: execution.BackendSteps);
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
            backendResult.ExportResult.PayloadPreview,
            adapterWorkflows,
            normalizedRevision);
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

    [Description(
        "Generate a PR-ready desired-state change set from a validated metadata release package (issue #57, first slice). " +
        "Input is the server-supplied release-package JSON: semantic resources, target environments, compatibility report, " +
        "optional data-script coverage, and rollback policy. Returns a deterministic, secret-free change set: a stable " +
        "branch name, commit message, PR title/body with evidence links, a repo-relative file path -> content map " +
        "(metadata manifests, environment overlays, optional data-script coverage, validation evidence, rollback policy), " +
        "rollback commands derived from the rollback classification and known-good revision, and an overall readiness of " +
        "ready/warning/blocked/unknown. Read-only: it renders Git artifacts in-process and never writes Git, opens a PR, " +
        "applies manifests, or creates a server operation; merge/reconcile runs through the governed GitOps/approval path. " +
        "Returns a blocked change set when compatibility flags breaking changes, and an unknown projection when the " +
        "document cannot be parsed.")]
    public Task<OperationResponse> GenerateMetadataReleaseChangeSetAsync(
        string releasePackageJson,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!MetadataReleaseChangeSetBuilder.TryBuild(releasePackageJson, out MetadataReleaseChangeSet changeSet, out string? error))
        {
            return Task.FromResult(new OperationResponse(
                Status: MetadataChangeSetReadiness.Unknown,
                Summary: "Metadata release package could not be turned into a change set: the supplied document was empty or malformed.",
                Findings:
                [
                    $"Parse error: {error ?? "unknown"}",
                    "No desired-state files were generated."
                ],
                Actions:
                [
                    "Supply a valid metadata release-package JSON document (server compatibility report, semantic resources, environments, rollback policy).",
                    "No Git artifacts were produced; nothing was written."
                ],
                ValidationChecks:
                [
                    "release-package-json-required",
                    "change-set-read-only-no-git-write"
                ],
                Risks:
                [
                    "Readiness is unknown because no release-package evidence could be interpreted."
                ]));
        }

        List<string> findings =
        [
            $"Release package: {changeSet.ReleasePackageId} ({changeSet.Service} -> {string.Join(", ", changeSet.TargetEnvironments)} @ {changeSet.DesiredRevision})",
            $"Readiness: {changeSet.Readiness}",
            $"Branch: {changeSet.BranchName}",
            $"Semantic resources: {changeSet.SemanticResources.Count}",
            $"Generated files: {changeSet.Files.Count} ({string.Join(", ", changeSet.Files.Select(file => file.Path))})",
            $"Rollback classification: {changeSet.RollbackClassification}; known-good revision: {changeSet.KnownGoodRevision ?? "unknown"}",
            $"Evidence references: {changeSet.EvidenceLinks.Count}"
        ];
        findings.AddRange(changeSet.BlockingReasons.Select(reason => $"Blocking: {reason}"));

        List<string> actions =
        [
            "Change set is read-only; honua-devops renders Git artifacts but never writes Git, opens a PR, or applies manifests.",
            $"Open or update branch `{changeSet.BranchName}` with the generated files, then route merge/reconcile through the governed GitOps/approval path.",
            .. changeSet.RollbackCommands.Count > 0
                ? new[] { $"Rollback commands prepared ({changeSet.RollbackCommands.Count}); they require explicit governed approval before running." }
                : Array.Empty<string>(),
            .. changeSet.Warnings.Select(warning => $"Warning: {warning}")
        ];

        return Task.FromResult(new OperationResponse(
            Status: changeSet.Readiness,
            Summary: $"Metadata release change set for `{changeSet.Service}` {changeSet.DesiredRevision} -> {string.Join(", ", changeSet.TargetEnvironments)} ({changeSet.Readiness}).",
            Findings: findings,
            Actions: actions,
            ValidationChecks:
            [
                "change-set-read-only-no-git-write",
                "branch-and-commit-deterministic",
                "generated-files-secret-free",
                "rollback-commands-derived-from-classification",
                "compatibility-report-interpreted-not-computed"
            ],
            Risks:
            [
                changeSet.Readiness == MetadataChangeSetReadiness.Blocked
                    ? "Change set is blocked by the supplied compatibility report; do not open a PR until the breaking change is resolved."
                    : "Change set reflects the supplied release-package evidence and can drift before merge.",
                changeSet.RollbackClassification == MetadataRollbackClass.Irreversible
                    ? "Rollback is classified irreversible; only a forward fix can recover this release."
                    : "Acting on the change set still requires the governed approval/submit path."
            ],
            MetadataReleaseChangeSet: changeSet));
    }

    [Description(
        "Plan a metadata-release-aware honua-gitops run from a validated metadata release package (issue #57 fast-follow). " +
        "Input is the same server-supplied release-package JSON consumed by generate_metadata_release_changeset. Fuses the " +
        "PR-ready change set with the honua-gitops planner so a single read-only output carries BOTH the desired-state change " +
        "set AND a metadata-release-aware gitops plan: per-environment diff/drift/state transitions tagged in-scope vs " +
        "not-targeted, plus a metadata-release summary (semantic resources, compatibility verdict, breaking-change count, " +
        "script coverage, rollback classification, known-good revision, blocking reasons). Default-safe and deterministic: it " +
        "never calls the backend, submits, rolls back, or mutates state; merge/reconcile runs through the governed " +
        "GitOps/approval path. Returns a blocked plan (surfacing blocking reasons) when compatibility flags breaking changes, " +
        "and a graceful unknown response when the release-package document cannot be parsed.")]
    public Task<OperationResponse> PlanMetadataReleaseGitOpsAsync(
        string releasePackageJson,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!MetadataReleaseChangeSetBuilder.TryBuild(releasePackageJson, out MetadataReleaseChangeSet changeSet, out string? error))
        {
            return Task.FromResult(new OperationResponse(
                Status: MetadataChangeSetReadiness.Unknown,
                Summary: "Metadata release package could not be turned into a gitops plan: the supplied document was empty or malformed.",
                Findings:
                [
                    $"Parse error: {error ?? "unknown"}",
                    "No change set and no gitops plan were produced."
                ],
                Actions:
                [
                    "Supply a valid metadata release-package JSON document (server compatibility report, semantic resources, environments, rollback policy).",
                    "No Git artifacts were produced and no backend call was made; nothing was written."
                ],
                ValidationChecks:
                [
                    "release-package-json-required",
                    "metadata-release-gitops-plan-read-only-no-backend-call"
                ],
                Risks:
                [
                    "Readiness is unknown because no release-package evidence could be interpreted."
                ]));
        }

        // Project the change set's service / target environments / desired revision onto the
        // existing honua-gitops planner. This is read-only and backend-free: an offline backend
        // result (no manifest export, no capability probe) feeds the planner so the plan is fully
        // derived from the supplied release package and deterministic for a given input.
        string normalizedGitOpsTool = ValidateGitOpsTool(runtime.GitOpsTool);
        string planService = ValidateServiceName(changeSet.Service);
        string planRevision = ValidateRevision(changeSet.DesiredRevision, "desired revision");
        IReadOnlyList<string> planEnvironments = changeSet.TargetEnvironments;
        const string policyGate = "metadata-release-gitops-plan";

        using GitOpsDeployBackendResult backendResult = BuildOfflineGitOpsBackendResult();

        RuntimeAdapterRequest adapterRequest = new(
            Service: planService,
            Environments: planEnvironments,
            Revision: planRevision,
            Action: "plan",
            ChangeSummary: $"metadata release {changeSet.ReleasePackageId}",
            GitOpsTool: normalizedGitOpsTool,
            TerraformRepository: SanitizePayloadValue(runtime.TerraformRepository, "terraform repository"),
            TerraformRef: ValidateRevision(runtime.TerraformRef, "terraform ref"),
            TerraformLocalPath: runtime.TerraformLocalPath,
            DryRun: true,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows = RuntimeAdapterRegistry
            .ResolveMany(ValidateDeploymentTargets(runtime.TerraformDeploymentTargets))
            .Select(adapter => adapter.BuildWorkflow(adapterRequest))
            .ToArray();

        ReleaseOrchestrationPlan releaseOrchestration = ReleaseOrchestrationPlanner.Build(
            adapterWorkflows,
            planEnvironments,
            requestedAction: "plan",
            dryRun: true,
            policyGate);
        ServiceBundleReconciliationPlan serviceBundleReconciliation = ServiceBundleReconciliationPlanner.Build(
            planService,
            planEnvironments,
            gateway.Configuration,
            backendResult.CapabilitiesPayload,
            backendResult.CapabilitiesResult.PayloadPreview,
            backendResult.ExportPayload,
            backendResult.ExportResult.PayloadPreview);
        GitOpsPlan gitOpsPlan = GitOpsPlanner.Build(
            planService,
            planEnvironments,
            planRevision,
            requestedAction: "plan",
            normalizedGitOpsTool,
            dryRun: true,
            policyGate,
            backendResult,
            releaseOrchestration,
            serviceBundleReconciliation);
        gitOpsPlan = GitOpsPlanner.AttachMetadataRelease(gitOpsPlan, changeSet);
        GitOpsMetadataReleaseSummary metadataRelease = gitOpsPlan.MetadataRelease!;

        List<string> findings =
        [
            $"Release package: {changeSet.ReleasePackageId} ({changeSet.Service} -> {string.Join(", ", changeSet.TargetEnvironments)} @ {changeSet.DesiredRevision})",
            $"Readiness: {changeSet.Readiness}",
            $"Semantic resources: {metadataRelease.SemanticResources.Count}",
            $"Compatibility: {metadataRelease.CompatibilityStatus} (breaking={metadataRelease.BreakingChanges}, warnings={metadataRelease.Warnings}).",
            $"Script coverage: {metadataRelease.ScriptCoverage}.",
            $"Rollback classification: {metadataRelease.RollbackClassification}; known-good revision: {metadataRelease.KnownGoodRevision ?? "unknown"}.",
            $"GitOps diff summary: {gitOpsPlan.DiffSummary}.",
            $"GitOps drift summary: {gitOpsPlan.DriftSummary}.",
            $"GitOps actual state source: {gitOpsPlan.ActualStateSource}."
        ];
        findings.AddRange(changeSet.BlockingReasons.Select(reason => $"Blocking: {reason}"));

        List<string> actions =
        [
            "Metadata-release gitops plan is read-only; honua-devops fuses the change set with the planner in-process and never writes Git, opens a PR, applies manifests, calls the backend, or creates a server operation.",
            $"Review the metadata-release-aware plan, then route merge/reconcile for branch `{changeSet.BranchName}` through the governed GitOps/approval path."
        ];
        actions.AddRange(BuildMetadataReleasePlanActions(metadataRelease));
        actions.AddRange(BuildGitOpsPlanActions(gitOpsPlan));

        return Task.FromResult(new OperationResponse(
            Status: changeSet.Readiness,
            Summary: $"Metadata-release gitops plan for `{changeSet.Service}` {changeSet.DesiredRevision} -> {string.Join(", ", changeSet.TargetEnvironments)} ({changeSet.Readiness}; compatibility {metadataRelease.CompatibilityStatus}).",
            Findings: findings,
            Actions: actions,
            ValidationChecks:
            [
                "metadata-release-gitops-plan-read-only-no-backend-call",
                "plan-derived-from-release-package-deterministic",
                "compatibility-report-interpreted-not-computed",
                "metadata-target-status-tagged-per-environment",
                ..gitOpsPlan.RequiredEvidence,
                "Review state transitions before approving any write-capable GitOps path."
            ],
            Risks:
            [
                changeSet.Readiness == MetadataChangeSetReadiness.Blocked
                    ? "Plan is blocked by the supplied compatibility report; do not reconcile until the breaking change is resolved."
                    : "Plan reflects the supplied release-package evidence and can drift before merge.",
                metadataRelease.RollbackClassification == MetadataRollbackClass.Irreversible
                    ? "Rollback is classified irreversible; only a forward fix can recover this release."
                    : "Acting on the plan still requires the governed approval/submit path.",
                "Snapshot-only planning can drift from reality if actual state changes before apply."
            ],
            GitOpsPlan: gitOpsPlan,
            ReleaseOrchestration: releaseOrchestration,
            ServiceBundleReconciliation: serviceBundleReconciliation,
            MetadataReleaseChangeSet: changeSet));
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
        return new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "solution-ready" : "backend-error")
            .Summary($"Deployment recommendation request targeting {cloud}.")
            .WithBackend(backendResult, "Honua API")
            .AddFinding($"Terraform template source: {runtime.TerraformRepository}@{runtime.TerraformRef}")
            .AddFinding($"Recommended deployment targets: {string.Join(", ", recommendedTargets)}")
            .AddActions([
                "Map customer requirements to validated Terraform deployment templates.",
                "Select target runtime from validated set: azure-functions, lambda, eks, aks, ecs, aca.",
                "Select topology by risk and performance profile: WAF/no-WAF, nginx/no-proxy, edge rate limiting.",
                "Produce staged GitOps rollout with rollback and operational ownership."
            ])
            .AddValidationChecks([
                $"Budget profile ({Normalize(budgetProfile, "balanced")}) aligns with recommended architecture.",
                "Proposed design satisfies required security and availability constraints."
            ])
            .AddRisks([
                "Missing non-functional requirements can produce under- or over-sized topology.",
                "Cost-only optimization can undercut resiliency for critical workloads."
            ])
            .Build();
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

        return new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "plan-ready" : "backend-error")
            .Summary($"Topology recommendation for `{environment}`.")
            .WithBackend(backendResult, "Honua API")
            .AddFinding($"Terraform template source: {runtime.TerraformRepository}@{runtime.TerraformRef}")
            .AddFinding($"Validated deployment targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}")
            .AddActions([
                $"WAF decision: {(enableWaf ? "enable with managed protections" : "disabled; enforce compensating controls at edge")}.",
                $"Ingress decision: {(useNginxProxy ? "nginx policy gateway" : "direct ingress with service-level policy controls")}.",
                $"Rate limiting: {(enableEdgeRateLimiting ? "enforce at edge" : "enforce via service policy + monitoring")}.",
                "Select matching Terraform template module and roll out through GitOps."
            ])
            .AddValidationChecks([
                "Synthetic load test confirms latency and failure behavior targets.",
                "Security controls align with risk tolerance and compliance needs."
            ])
            .AddRisks([
                "No-WAF posture increases exposure to application-layer attacks.",
                "Skipping validated template modules can introduce config drift."
            ])
            .Build();
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

        OperationResponseBuilder builder = new OperationResponseBuilder()
            .Status(backendResult.IsSuccess ? "slow-query-explained" : "backend-error")
            .Summary($"Slow query analysis for `{normalizedService}` in `{normalizedEnvironment}`.")
            .WithBackend(backendResult, "OTEL logs");

        if (Contains(sample, "seq scan", "full scan"))
            builder.AddFinding("Sequential scan indicators suggest missing attribute or spatial selectivity.");
        if (Contains(sample, "st_intersects", "bbox", "geometry"))
            builder.AddFinding("Spatial predicate present; verify spatial index coverage and bounding-box prefiltering.");
        if (Contains(sample, "cache miss", "miss ratio"))
            builder.AddFinding("Cache miss indicators suggest TTL, key cardinality, or seeding review.");

        return builder
            .AddActions([
                "Compare slow query predicates against available spatial and attribute indexes.",
                "Add bounding-box prefilters before expensive geometry predicates when possible.",
                "Tune cache TTL and seeding only after query shape and index coverage are validated."
            ])
            .AddValidationChecks([
                "Explain plan uses the expected spatial or compound index.",
                "P95 query latency improves under representative load."
            ])
            .AddRisks([
                "Index recommendations based on a single query can hurt write-heavy workloads.",
                "Cache tuning without query fixes can hide persistent database pressure."
            ])
            .Build();
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

    [Description(
        "Plan a deliverable's draft->preview->approved->published lifecycle (issue #77) bound to environments. " +
        "Read-only planner: it produces the lifecycle plan (ordered transitions, per-step target environment, gate, " +
        "required evidence, edition requirement, and the governed Console approval action) and does NOT generate the " +
        "deliverable artifact, execute promotion, or mutate state. Inputs: workItemId + kind identify the deliverable; " +
        "currentState (draft/preview/approved/published, default draft) is where the lifecycle starts; lowerEnvironment " +
        "is the preview/approval target; publishEnvironment (default prod) is the cross-environment promotion target. " +
        "Edition gating: single-environment draft->preview->approved is Pro; cross-environment approved->published " +
        "(prod through deploy-control gated-promotion) is Enterprise — below the required edition the corresponding " +
        "step is surfaced as edition-gated rather than executed. The Preview->Approved gate is emitted as a governed " +
        "SuggestedAction with requiresApproval=true via the Console approval surface; Approved->Published reuses the " +
        "release-orchestration gated-promotion engine (approval-record + lower-env-evidence + smoke-contract + " +
        "slo-gate-evidence). dryRun is always true.")]
    public Task<OperationResponse> PlanDeliverableLifecycleAsync(
        string workItemId,
        string kind,
        string currentState,
        string lowerEnvironment,
        string publishEnvironment,
        string previewUrl,
        string edition,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        string normalizedWorkItemId = SanitizePayloadValue(workItemId, "work item ID");
        string normalizedKind = Normalize(kind, "deliverable").Trim().ToLowerInvariant();
        DeliverableLifecycleState currentLifecycleState = DeliverableLifecycleStateExtensions.ParseOrDraft(currentState);
        string normalizedEdition = NormalizeEdition(edition);

        // Pro is the floor for the single-environment lifecycle (draft->preview->approved).
        if (!EditionAtLeast(normalizedEdition, "pro"))
        {
            return Task.FromResult(BuildEditionGateResponse("plan_deliverable_lifecycle", normalizedEdition, "pro"));
        }

        // Resolve the lower (preview/approval) and publish (promotion) environments against
        // the allowed set; never plan against an environment the runtime does not permit.
        string normalizedLowerEnvironment = ParseEnvironments(lowerEnvironment).First();
        string normalizedPublishEnvironment = ParseEnvironments(Normalize(publishEnvironment, "prod")).First();
        string? normalizedPreviewUrl = string.IsNullOrWhiteSpace(previewUrl)
            ? null
            : SanitizePayloadValue(previewUrl, "preview URL");

        string deliverableId = $"{normalizedWorkItemId}:{normalizedKind}";
        string approvalOperationId = $"deliverable-lifecycle:{deliverableId}";

        bool enterpriseUnlocked = EditionAtLeast(normalizedEdition, "enterprise");

        // Reuse the release-orchestration gated-promotion engine for Approved->Published
        // rather than writing a new promotion engine. Only build it when the cross-env step
        // is both planned (from Approved or earlier) and unlocked at Enterprise; below
        // Enterprise the step is surfaced as edition-gated with no executable plan.
        bool crossEnvPlanned = currentLifecycleState <= DeliverableLifecycleState.Approved;
        ReleaseOrchestrationPlan? promotionPlan = null;
        if (crossEnvPlanned && enterpriseUnlocked)
        {
            string[] promotionEnvironments = new[] { normalizedLowerEnvironment, normalizedPublishEnvironment }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RuntimeAdapterRequest adapterRequest = new(
                Service: deliverableId,
                Environments: promotionEnvironments,
                Revision: "deliverable-artifact",
                Action: "promote",
                ChangeSummary: $"publish deliverable `{deliverableId}` from `{normalizedLowerEnvironment}` to `{normalizedPublishEnvironment}`",
                GitOpsTool: ValidateGitOpsTool(runtime.GitOpsTool),
                TerraformRepository: SanitizePayloadValue(runtime.TerraformRepository, "terraform repository"),
                TerraformRef: ValidateRevision(runtime.TerraformRef, "terraform ref"),
                TerraformLocalPath: runtime.TerraformLocalPath,
                DryRun: true,
                ExecutionMode: runtime.ExecutionMode,
                ExecutionTier: runtime.ExecutionTier);
            IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows = RuntimeAdapterRegistry
                .ResolveMany(ValidateDeploymentTargets(runtime.TerraformDeploymentTargets))
                .Select(adapter => adapter.BuildWorkflow(adapterRequest))
                .ToArray();
            promotionPlan = ReleaseOrchestrationPlanner.Build(
                adapterWorkflows,
                promotionEnvironments,
                "promote",
                dryRun: true,
                "deliverable-publish-gated-promotion");
        }

        Operations.Deliverable.Deliverable deliverable = new(
            DeliverableId: deliverableId,
            WorkItemId: normalizedWorkItemId,
            Kind: normalizedKind,
            State: currentLifecycleState,
            Environment: normalizedLowerEnvironment,
            PreviewUrl: normalizedPreviewUrl,
            Provenance:
            [
                new EvidenceRef(
                    Type: "work-item",
                    Source: "work-intake",
                    RawRef: normalizedWorkItemId,
                    Url: null,
                    Summary: $"Deliverable `{deliverableId}` references work item `{normalizedWorkItemId}`.",
                    CapturedAt: "planner",
                    Sensitivity: EvidenceSensitivity.Internal)
            ]);

        IDeliverableApprovalTrigger approvalTrigger = new ConsoleApprovalTrigger();
        DeliverableLifecyclePlan lifecyclePlan = DeliverableLifecyclePlanner.Build(
            deliverable,
            normalizedLowerEnvironment,
            normalizedPublishEnvironment,
            normalizedEdition,
            approvalTrigger,
            promotionPlan,
            approvalOperationId);
        DeliverableProjection projection = DeliverableProjection.From(lifecyclePlan, deliverable);
        SuggestedAction? approvalAction = DeliverableLifecyclePlanner.FindApprovalAction(lifecyclePlan);

        List<string> findings =
        [
            $"Deliverable: {deliverableId} (kind={normalizedKind}, work item={normalizedWorkItemId}).",
            $"Current state: {currentLifecycleState.ToConfigValue()}; lower env={normalizedLowerEnvironment}; publish env={normalizedPublishEnvironment}.",
            $"Caller edition: {normalizedEdition}; cross-env promotion planned={crossEnvPlanned}, unlocked={enterpriseUnlocked}.",
            $"Preview link available: {!string.IsNullOrWhiteSpace(normalizedPreviewUrl)} (never fabricated)."
        ];
        findings.AddRange(lifecyclePlan.Transitions.Select(transition =>
            $"Transition {transition.FromState.ToConfigValue()} -> {transition.ToState.ToConfigValue()}: " +
            $"env={transition.TargetEnvironment}, gate={transition.Gate}, edition={transition.RequiredEdition}, " +
            $"evidence=[{string.Join(", ", transition.RequiredEvidence)}]."));

        List<string> actions =
        [
            "Lifecycle is plan-only: honua-devops does not generate the artifact, execute promotion, or mutate deliverable state here.",
            $"Draft -> Preview renders in lower environment `{normalizedLowerEnvironment}` (Pro); write the preview link and provenance card back to the work item.",
            "Preview -> Approved routes the governed Console approval action through the approval surface; honua-devops never approves on its own."
        ];
        if (approvalAction is not null)
        {
            actions.Add($"Approval action `{approvalAction.Id}` (requiresApproval={approvalAction.RequiresApproval}, source={projection.Transitions.First(t => t.ToState == "approved").ApprovalSource}).");
        }
        if (crossEnvPlanned && enterpriseUnlocked)
        {
            actions.Add($"Approved -> Published promotes to `{normalizedPublishEnvironment}` via the gated-promotion engine (Enterprise); requires {string.Join(" + ", DeliverableLifecyclePlanner.PublishEvidence)}.");
        }
        else if (crossEnvPlanned)
        {
            actions.Add($"Approved -> Published is edition-gated: cross-environment promotion to `{normalizedPublishEnvironment}` requires Enterprise (current edition `{normalizedEdition}`).");
        }

        return Task.FromResult(new OperationResponse(
            Status: "deliverable-lifecycle-plan",
            Summary: $"Deliverable lifecycle plan for `{deliverableId}` ({currentLifecycleState.ToConfigValue()} -> published) across `{normalizedLowerEnvironment}` -> `{normalizedPublishEnvironment}`.",
            Findings: findings,
            Actions: actions,
            ValidationChecks:
            [
                "deliverable-lifecycle-read-only-no-artifact-generation",
                "deliverable-lifecycle-no-promotion-execution",
                "preview-link-not-fabricated",
                "preview-to-approved-requires-governed-approval",
                .. DeliverableLifecyclePlanner.FlattenEvidenceRequirements(lifecyclePlan)
            ],
            Risks:
            [
                "Plan reflects the supplied work item and environments and can drift before the artifact is built.",
                "Cross-environment promotion to prod must remain Enterprise-gated and routed through the governed deploy-control path.",
                "Preview -> Approved must clear the Console approval surface; never auto-advance the lifecycle."
            ],
            DeliverableLifecycle: projection));
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
        => DeploymentInputs.Normalize(value, fallback);

    // Project a honua-support ticket onto the support-context-v1 superset (honua-console#170).
    // Prefer the structured `context` block the console persisted on the ticket; fall back to
    // the ticket's own top-level fields for what the console did not supply, and map the
    // triage-resolved `environment` onto the contract's envKind enum. Only fields devops
    // actually knows are populated — absent values are left null so SupportGateway omits them.
    private static SupportContext BuildAutoBundleContext(
        System.Text.Json.JsonElement ticketElement,
        string environment)
    {
        bool hasContext = ticketElement.TryGetProperty("context", out System.Text.Json.JsonElement contextEl)
            && contextEl.ValueKind == System.Text.Json.JsonValueKind.Object;

        string? ContextString(string name)
            => hasContext ? ReadString(contextEl, name) : null;

        string? envKind = ContextString("envKind") ?? MapEnvKind(environment);
        string? appVersion = ContextString("appVersion") ?? ReadString(ticketElement, "appVersion");
        string? commit = ContextString("commit") ?? ReadString(ticketElement, "commit");
        string? route = ContextString("route") ?? ReadString(ticketElement, "route");

        SupportContextUser? user = null;
        SupportContextTenant? tenant = null;
        IReadOnlyList<SupportContextRecentError>? recentErrors = null;
        if (hasContext)
        {
            user = BuildContextUser(contextEl);
            tenant = BuildContextTenant(contextEl);
            recentErrors = BuildContextRecentErrors(contextEl);
        }

        // Fall back to a top-level ticket `tenant` object/string when the console context did
        // not carry one, so the owning customer still rides along when honua-support knows it.
        tenant ??= BuildTopLevelTenant(ticketElement);

        return new SupportContext(
            User: user,
            Tenant: tenant,
            EnvKind: envKind,
            AppVersion: appVersion,
            Commit: commit,
            Route: route,
            RecentErrors: recentErrors,
            InstanceUrl: ContextString("instanceUrl"),
            ScopedKey: null);
    }

    private static SupportContextUser? BuildContextUser(System.Text.Json.JsonElement contextEl)
    {
        if (!contextEl.TryGetProperty("user", out System.Text.Json.JsonElement userEl)
            || userEl.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        string? id = ReadString(userEl, "id");
        string? email = ReadString(userEl, "email");
        string? displayName = ReadString(userEl, "displayName");
        if (id is null && email is null && displayName is null)
        {
            return null;
        }

        return new SupportContextUser(id, email, displayName);
    }

    private static SupportContextTenant? BuildContextTenant(System.Text.Json.JsonElement contextEl)
    {
        if (!contextEl.TryGetProperty("tenant", out System.Text.Json.JsonElement tenantEl)
            || tenantEl.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        string? id = ReadString(tenantEl, "id");
        string? name = ReadString(tenantEl, "name");
        return id is null && name is null ? null : new SupportContextTenant(id, name);
    }

    private static SupportContextTenant? BuildTopLevelTenant(System.Text.Json.JsonElement ticketElement)
    {
        if (ticketElement.TryGetProperty("tenant", out System.Text.Json.JsonElement tenantEl))
        {
            if (tenantEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                string? id = ReadString(tenantEl, "id");
                string? name = ReadString(tenantEl, "name");
                return id is null && name is null ? null : new SupportContextTenant(id, name);
            }

            if (tenantEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string? value = tenantEl.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : new SupportContextTenant(Id: value);
            }
        }

        return ReadString(ticketElement, "tenantId") is { } tenantId
            ? new SupportContextTenant(Id: tenantId)
            : null;
    }

    private static IReadOnlyList<SupportContextRecentError>? BuildContextRecentErrors(System.Text.Json.JsonElement contextEl)
    {
        if (!contextEl.TryGetProperty("recentErrors", out System.Text.Json.JsonElement errorsEl)
            || errorsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        List<SupportContextRecentError> errors = [];
        foreach (System.Text.Json.JsonElement errorEl in errorsEl.EnumerateArray())
        {
            if (errorEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            string? timestamp = ReadString(errorEl, "timestamp");
            string? message = ReadString(errorEl, "message");
            string? correlationId = ReadString(errorEl, "correlationId");
            string? path = ReadString(errorEl, "path");
            int? statusCode = errorEl.TryGetProperty("statusCode", out System.Text.Json.JsonElement statusEl)
                && statusEl.ValueKind == System.Text.Json.JsonValueKind.Number
                && statusEl.TryGetInt32(out int parsedStatus)
                    ? parsedStatus
                    : null;

            if (timestamp is null && message is null && correlationId is null && path is null && statusCode is null)
            {
                continue;
            }

            errors.Add(new SupportContextRecentError(timestamp, message, correlationId, path, statusCode));
        }

        return errors.Count == 0 ? null : errors;
    }

    private static string? ReadString(System.Text.Json.JsonElement element, string name)
        => element.TryGetProperty(name, out System.Text.Json.JsonElement value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

    // Map a free-text ticket environment onto the support-context-v1 envKind enum
    // (saas | on-prem | dedicated | dev | staging | production | unknown). Drives whether the
    // honua-support collector pulls telemetry server-side or requests an on-prem bundle.
    // Returns null for an absent/"unknown" environment so the field is omitted rather than
    // asserting a topology devops cannot confirm.
    private static string? MapEnvKind(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        return environment.Trim().ToLowerInvariant() switch
        {
            "saas" => "saas",
            "on-prem" or "onprem" or "on_prem" => "on-prem",
            "dedicated" => "dedicated",
            "dev" or "development" => "dev",
            "staging" or "stage" => "staging",
            "prod" or "production" => "production",
            "unknown" => null,
            _ => null
        };
    }

    private string[] ParseEnvironments(string value)
        => DeploymentInputs.ParseEnvironments(value, runtime.AllowedEnvironments);

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
        => DeploymentInputs.ValidateServiceName(value);

    private static string ValidateRevision(string value, string fieldName)
        => DeploymentInputs.ValidateRevision(value, fieldName);

    private static string ValidateAction(string value)
        => DeploymentInputs.ValidateAction(value);

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
        => DeploymentInputs.SanitizeFreeText(value, fallback);

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

    // Surface the metadata-release projection as plan action lines so an agent/Console reading the
    // response sees the compatibility verdict, semantic resources, script coverage, rollback posture,
    // and any blocking reasons alongside the gitops diff/drift/transition lines.
    private static IEnumerable<string> BuildMetadataReleasePlanActions(GitOpsMetadataReleaseSummary summary)
    {
        yield return $"Metadata release `{summary.ReleasePackageId}` readiness: {summary.Readiness}; compatibility: {summary.CompatibilityStatus} (breaking={summary.BreakingChanges}, warnings={summary.Warnings}).";
        yield return $"Metadata release script coverage: {summary.ScriptCoverage}.";
        yield return $"Metadata release rollback classification: {summary.RollbackClassification}; known-good revision: {summary.KnownGoodRevision ?? "unknown"}.";

        foreach (MetadataResourceSummary resource in summary.SemanticResources)
        {
            yield return $"Metadata semantic resource `{resource.Kind}/{resource.Name}`: {resource.Action}.";
        }

        foreach (string reason in summary.BlockingReasons)
        {
            yield return $"Metadata release blocking reason: {reason}";
        }
    }

    // Build an offline gitops backend result for the metadata-release plan path: no manifest export,
    // no capability probe, no network. The planner treats the absent payloads as actual-state-pending
    // so the plan is derived solely from the supplied release package and is deterministic.
    private GitOpsDeployBackendResult BuildOfflineGitOpsBackendResult()
    {
        BackendCallResult exportSkipped = new(
            IsSuccess: false,
            Endpoint: BackendGateway.BuildEndpoint(gateway.Configuration.HonuaApiBaseUri, gateway.Configuration.HonuaManifestExportPath).ToString(),
            Detail: "metadata-release plan: manifest export skipped (read-only, no backend call)",
            PayloadPreview: "export not requested");
        BackendCallResult capabilitiesSkipped = new(
            IsSuccess: false,
            Endpoint: BackendGateway.BuildEndpoint(gateway.Configuration.HonuaApiBaseUri, gateway.Configuration.HonuaAdminCapabilitiesPath).ToString(),
            Detail: "metadata-release plan: capability probe skipped (read-only, no backend call)",
            PayloadPreview: "capabilities not requested");
        BackendCallResult applySkipped = new(
            IsSuccess: true,
            Endpoint: BackendGateway.BuildEndpoint(gateway.Configuration.HonuaApiBaseUri, gateway.Configuration.HonuaManifestApplyPath).ToString(),
            Detail: "metadata-release plan: manifest apply skipped (read-only)",
            PayloadPreview: "apply not requested");

        return new GitOpsDeployBackendResult(
            ApplyResult: applySkipped,
            ExportResult: exportSkipped,
            CapabilitiesResult: capabilitiesSkipped,
            CombinedResult: exportSkipped,
            ExportPayload: null,
            CapabilitiesPayload: null);
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
