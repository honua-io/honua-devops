using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.ConsoleBridge;

// Projection layer that packages existing honua-devops operation models and landed
// honua-server deploy-control facts into stable, evidence-linked Console contracts.
//
// Identity rules (see issue #59 design):
//   * The honua-server deploy-control operationId is the Console-facing stable workflow
//     ID and is reused unchanged across proposal/PR/CI/promotion/SLO/rollback views.
//   * Idempotency keys derive from operational scope, never from prose.
//   * When no durable server operation can be created (no target, or the contract is
//     unavailable) the bridge returns a blocked projection rather than inventing an ID.
//   * AI output stays advisory: nothing here submits, rolls back, or applies manifests.
internal sealed class ConsoleOperationBridge(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel? policy = null)
{
    private const string OperationKind = "gitops-deploy";
    private const int MaxReadableKeyLength = 200;
    private const string AgentIdentity = "honua-devops";

    private OperatorPolicyModel EffectivePolicy => policy ?? OperatorPolicyModel.Default;

    private BackendConfiguration Configuration => gateway.Configuration;

    [Description("Create a GitOps deployment proposal as a stable, evidence-linked projection. Records a durable honua-server deploy-control operation with submitImmediately=false (advisory only; never executes). Returns the proposal with the server operationId, deterministic idempotency key, raw backend evidence references, workflow deep links, and governed submit/rollback suggestions that require explicit approval. Returns a blocked projection if no deploy target is configured.")]
    public async Task<OperationResponse> CreateGitOpsProposalAsync(
        string service,
        string environmentsCsv,
        string revision,
        string action,
        string changeSummary,
        string owner,
        CancellationToken cancellationToken = default)
    {
        string normalizedService = DeploymentInputs.ValidateServiceName(service);
        string[] environments = DeploymentInputs.ParseEnvironments(environmentsCsv, runtime.AllowedEnvironments);
        string normalizedRevision = DeploymentInputs.ValidateRevision(DeploymentInputs.Normalize(revision, "HEAD"), "revision");
        string normalizedAction = DeploymentInputs.ValidateAction(action);
        string normalizedChangeSummary = DeploymentInputs.SanitizeFreeText(changeSummary, "not provided");
        string normalizedOwner = DeploymentInputs.SanitizeFreeText(owner, "unassigned");

        bool targetsProd = environments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        bool approvalRequired = EffectivePolicy.ApprovalMode != ApprovalMode.DirectAllowed || targetsProd;
        string timestamp = Timestamp();
        string deployTargetId = DeploymentInputs.Normalize(runtime.DeployTargetId, "unconfigured");
        string idempotencyKey = BuildProposalIdempotencyKey(
            deployTargetId,
            normalizedService,
            environments,
            normalizedRevision,
            normalizedAction);

        // No durable target: stay blocked instead of minting a fake operation id.
        if (string.IsNullOrWhiteSpace(runtime.DeployTargetId))
        {
            GitOpsProposalBridge blocked = new(
                ProposalId: idempotencyKey,
                OperationId: null,
                IdempotencyKey: idempotencyKey,
                Status: BridgeStatus.TargetUnconfigured,
                Service: normalizedService,
                TargetEnvironments: environments,
                DesiredRevision: normalizedRevision,
                CurrentRevision: null,
                RequestedAction: normalizedAction,
                EffectiveAction: "propose",
                Owner: normalizedOwner,
                ApprovalRequired: approvalRequired,
                WorkflowLinks: [SelfLink(operationId: null)],
                Evidence: [],
                SuggestedActions: [ConfigureTargetSuggestion()],
                CreatedAt: timestamp,
                UpdatedAt: timestamp,
                Kind: OperationKind,
                Requester: normalizedOwner,
                Agent: AgentIdentity,
                // No durable target means the proposal cannot enter the lifecycle yet; stay
                // Planned (the lifecycle entry state) rather than asserting an approval state.
                ProposalStatus: ProposalLifecycle.Planned,
                Plan: BuildProposalPlan(
                    normalizedService,
                    environments,
                    normalizedRevision,
                    normalizedAction,
                    approvalRequired,
                    targetsProd,
                    blockingReasons: ["HONUA_DEVOPS_DEPLOY_TARGET_ID is not configured; cannot create a durable server operation."]));
            return BuildProposalResponse(
                blocked,
                backendSteps: null,
                blockingReason: "HONUA_DEVOPS_DEPLOY_TARGET_ID is not configured; cannot create a durable server operation.");
        }

        BackendCallResult preflight = await gateway.RequestDeployPreflightAsync(includeDiagnostics: true, cancellationToken);
        BackendCallResult plan = await gateway.PlanDeployOperationAsync(
            runtime.DeployTargetId,
            normalizedRevision,
            currentRevision: null,
            new Dictionary<string, string>
            {
                ["service"] = normalizedService,
                ["environments"] = string.Join(",", environments),
                ["action"] = normalizedAction,
                ["source"] = "honua-devops:proposal"
            },
            cancellationToken);
        using BackendJsonResult created = await gateway.CreateDeployOperationJsonAsync(
            runtime.DeployTargetId,
            normalizedRevision,
            currentRevision: null,
            reason: normalizedChangeSummary,
            submitImmediately: false,
            idempotencyKey: idempotencyKey,
            correlationId: $"honua-devops:proposal:{normalizedService}",
            priority: targetsProd ? "high" : "normal",
            parameters: new Dictionary<string, string>
            {
                ["service"] = normalizedService,
                ["environments"] = string.Join(",", environments),
                ["action"] = normalizedAction,
                ["owner"] = normalizedOwner,
                ["proposal"] = "true"
            },
            cancellationToken);

        string? operationId = created.Payload is null ? null : ExtractOperationId(created.Payload.RootElement);
        string? serverStatus = created.Payload is null ? null : ExtractStatus(created.Payload.RootElement);
        bool createdOperation = created.CallResult.IsSuccess && !string.IsNullOrWhiteSpace(operationId);
        string status = createdOperation ? BridgeStatus.Proposed : BridgeStatus.ContractUnavailable;
        // Canonical lifecycle value (1:1 with the server WorkflowOperationStatus). A recorded
        // proposal that the server has not advanced sits at AwaitingApproval when approval is
        // required, otherwise Planned; an unreadable/missing status falls back to Planned so
        // the proposal still enters the lifecycle rather than reporting unknown on create.
        string proposalStatus = createdOperation
            ? MapProposalLifecycle(serverStatus, approvalRequired)
            : ProposalLifecycle.Planned;

        List<OperationBackendStep> steps =
        [
            ToStep("deploy-preflight", preflight, mutatesState: false),
            ToStep("deploy-plan", plan, mutatesState: false),
            // Creating the operation persists a durable server record (idempotent), so it
            // is a real write even though submitImmediately=false means nothing executes.
            ToStep("deploy-operation-create", created.CallResult, mutatesState: createdOperation)
        ];

        List<EvidenceRef> evidence =
        [
            EvidenceFromCall("deploy-preflight", preflight),
            EvidenceFromCall("deploy-plan", plan),
            EvidenceFromCall("deploy-operation", created.CallResult)
        ];
        if (operationId is not null)
        {
            evidence.Add(ServerOperationEvidence(operationId));
        }

        List<WorkflowLink> links = [SelfLink(operationId)];
        if (operationId is not null)
        {
            links.Add(ServerOperationLink(operationId));
            links.Add(GovernedLink("submit", "Submit proposal for execution", operationId, "submit"));
            links.Add(GovernedLink("rollback", "Roll back operation", operationId, "rollback"));
        }

        List<SuggestedAction> suggestedActions = [];
        if (operationId is not null)
        {
            suggestedActions.Add(SubmitSuggestion(operationId));
            suggestedActions.Add(RollbackSuggestion(operationId));
        }
        suggestedActions.Add(ReviewEvidenceSuggestion(operationId));

        GitOpsProposalBridge proposal = new(
            ProposalId: idempotencyKey,
            OperationId: operationId,
            IdempotencyKey: idempotencyKey,
            Status: status,
            Service: normalizedService,
            TargetEnvironments: environments,
            DesiredRevision: normalizedRevision,
            CurrentRevision: null,
            RequestedAction: normalizedAction,
            EffectiveAction: "propose",
            Owner: normalizedOwner,
            ApprovalRequired: approvalRequired,
            WorkflowLinks: links,
            Evidence: evidence,
            SuggestedActions: suggestedActions,
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            Kind: OperationKind,
            Requester: normalizedOwner,
            Agent: AgentIdentity,
            ProposalStatus: proposalStatus,
            Plan: BuildProposalPlan(
                normalizedService,
                environments,
                normalizedRevision,
                normalizedAction,
                approvalRequired,
                targetsProd,
                blockingReasons: createdOperation
                    ? []
                    : ["honua-server deploy-control did not return a durable operation id; proposal is blocked, no operation invented."]));

        return BuildProposalResponse(
            proposal,
            steps,
            blockingReason: createdOperation
                ? null
                : "honua-server deploy-control did not return a durable operation id; proposal is blocked, no operation invented.");
    }

    [Description("Plan provisioning/updating the durable PER-ENVIRONMENT geoprocessing (GP) substrate and record it as a PLAN-FIRST, advisory deploy-control proposal (submitImmediately=false; never executes). The substrate is the AWS Batch compute-env (Fargate-Spot), job queue, IAM roles, ECR repo, and a POOL of job-definition ephemeral-storage tiers (s/m/l/xl = 20/50/100/200 GiB). It is provisioned RARELY (when GP capability is added/updated in an env), NOT per job. Per-env config: image (empty=reuse Lambda image), architecture (x86_64|arm64, shared by the whole tier pool), maxVcpus (compute-env ceiling), createWorkerGdalRepo, tiersCsv (subset of s,m,l,xl; empty=all four). The substrate exposes OUTPUTS the server binds to (gp_job_queue_arn, gp_job_definition_arns map, gp_compute_environment_arn, gp_job_role_arn, gp_execution_role_arn, gp_worker_gdal_repository_url) — never input-variable names. Per-JOB sizing (vCPU/memory/timeout/retry + tier selection) is a SubmitJob-time runtime concern with zero infra change; use plan_gp_job_sizing for that planning aid. The real terraform apply stays behind the agent's execution/approval gates exactly like the other runtime adapters.")]
    public async Task<OperationResponse> PlanGpSubstrateAsync(
        string service,
        string environmentsCsv,
        string architecture,
        string image,
        int maxVcpus,
        bool createWorkerGdalRepo,
        string tiersCsv,
        string owner,
        CancellationToken cancellationToken = default)
    {
        string normalizedService = DeploymentInputs.ValidateServiceName(service);
        string[] environments = DeploymentInputs.ParseEnvironments(environmentsCsv, runtime.AllowedEnvironments);
        string normalizedOwner = DeploymentInputs.SanitizeFreeText(owner, "unassigned");

        GpCpuArchitecture arch = ParseGpArchitecture(architecture);
        IReadOnlyList<GpJobDefinitionTier> tiers = ParseGpTiers(tiersCsv);
        GpSubstrateConfig substrate = new(
            Image: string.IsNullOrWhiteSpace(image) ? null : image.Trim(),
            Architecture: arch,
            MaxVcpus: maxVcpus <= 0 ? 256 : maxVcpus,
            CreateWorkerGdalRepo: createWorkerGdalRepo,
            Tiers: tiers);

        GpRuntimeAdapter adapter = new(substrate);
        IReadOnlyList<GpSubstrateVar> substrateVars = substrate.ToSubstrateVars();

        List<string> substrateFindings =
        [
            "Per-ENV GP substrate provision (rare, GitOps-gated). Per-job sizing is a SubmitJob-time runtime concern, NOT provisioned here.",
            $"honua-iac GP substrate inputs (gated {GpSubstrateConfig.EnableVar}=true):",
            .. substrateVars.Select(v => $"  {v.Name} = {v.Value}"),
            "Substrate OUTPUTS the server binds to (ARNs, not input-variable names):",
            .. GpSubstrateOutputs.All.Select(output => $"  output {output}")
        ];

        // Record the advisory proposal through the SAME plan-first, submitImmediately=false
        // deploy-control path the GitOps proposals use, then enrich it with the substrate step
        // plan + the per-env substrate var mapping and OUTPUT bindings. action=apply because the
        // proposal is a (gated) terraform apply of the durable substrate stack.
        string changeSummary = $"GP per-env substrate provision: {GpSubstrateDescriptor(substrateVars)}";
        OperationResponse proposal = await CreateGitOpsProposalAsync(
            normalizedService,
            string.Join(",", environments),
            revision: "HEAD",
            action: "apply",
            changeSummary: changeSummary,
            owner: normalizedOwner,
            cancellationToken);

        return EnrichGpProposal(proposal, adapter, substrateFindings);
    }

    [Description("Plan provisioning/updating the durable PER-ENVIRONMENT AZURE geoprocessing (GP) substrate and record it as a PLAN-FIRST, advisory deploy-control proposal (submitImmediately=false; never executes). The Azure equivalent of plan_gp_substrate. The substrate is the Azure Batch account, a SINGLE worker pool (VM size + node ceiling), the user-assigned task identity, the ACR holding the GDAL worker image, and the output blob container. It is provisioned RARELY (when GP capability is added/updated in an env), NOT per job. UNLIKE AWS, Azure sizes per-POOL and the MVP is a SINGLE pool, so there is NO tier selector. Per-env config: poolVmSize (e.g. Standard_D4s_v5, shared by the pool), maxNodes (pool autoscale ceiling; 0=>8), image (empty=ACR default), acrName, createWorkerGdalAcr. The substrate exposes OUTPUTS the server binds to (gp_batch_account_url, gp_pool_id, gp_batch_account_id, gp_task_identity_id, gp_task_identity_principal_id, gp_acr_login_server, gp_output_container_url, gp_control_plane_backend_name=honua-azure-batch) — never input-variable names. Per-JOB sizing (task timeout/retry/image) is a task-add-time runtime concern with zero infra change; use plan_azure_gp_job_sizing for that. The real terraform apply stays behind the agent's execution/approval gates exactly like the other runtime adapters.")]
    public async Task<OperationResponse> PlanAzureGpSubstrateAsync(
        string service,
        string environmentsCsv,
        string poolVmSize,
        string image,
        string acrName,
        int maxNodes,
        bool createWorkerGdalAcr,
        string owner,
        CancellationToken cancellationToken = default)
    {
        string normalizedService = DeploymentInputs.ValidateServiceName(service);
        string[] environments = DeploymentInputs.ParseEnvironments(environmentsCsv, runtime.AllowedEnvironments);
        string normalizedOwner = DeploymentInputs.SanitizeFreeText(owner, "unassigned");

        AzureGpSubstrateConfig substrate = new(
            Image: string.IsNullOrWhiteSpace(image) ? null : image.Trim(),
            AcrName: string.IsNullOrWhiteSpace(acrName) ? string.Empty : acrName.Trim(),
            PoolVmSize: string.IsNullOrWhiteSpace(poolVmSize) ? "Standard_D4s_v5" : poolVmSize.Trim(),
            MaxNodes: maxNodes <= 0 ? 8 : maxNodes,
            CreateWorkerGdalAcr: createWorkerGdalAcr);

        AzureGpRuntimeAdapter adapter = new(substrate);
        IReadOnlyList<AzureGpSubstrateVar> substrateVars = substrate.ToSubstrateVars();

        List<string> substrateFindings =
        [
            "Per-ENV Azure GP substrate provision (rare, GitOps-gated). Per-job sizing is a task-add-time runtime concern, NOT provisioned here.",
            "Azure sizes per-POOL; the MVP is a SINGLE pool, so there is NO tier selection (deferred fast-follow).",
            $"honua-iac azure-cert substrate passthrough inputs (the stack hardcodes {AzureGpSubstrateConfig.EnableVar}=true):",
            .. substrateVars.Select(v => $"  {v.Name} = {v.Value}"),
            "Substrate OUTPUTS the server binds to (not input-variable names):",
            .. AzureGpSubstrateOutputs.All.Select(output => $"  output {output}")
        ];

        // Record the advisory proposal through the SAME plan-first, submitImmediately=false
        // deploy-control path the GitOps proposals use, then enrich it with the substrate step
        // plan + the per-env substrate var mapping and OUTPUT bindings. action=apply because the
        // proposal is a (gated) terraform apply of the durable substrate stack.
        string changeSummary = $"Azure GP per-env substrate provision: {AzureGpSubstrateDescriptor(substrate)}";
        OperationResponse proposal = await CreateGitOpsProposalAsync(
            normalizedService,
            string.Join(",", environments),
            revision: "HEAD",
            action: "apply",
            changeSummary: changeSummary,
            owner: normalizedOwner,
            cancellationToken);

        return EnrichAzureGpProposal(proposal, adapter, substrateFindings);
    }

    [Description("Compute the runtime SIZING HINT for a single geoprocessing (GP) job: select the durable job-definition tier (s/m/l/xl = ephemeralStorageGib <=20/<=50/<=100/<=200) from the per-env substrate's pre-provisioned pool and produce the AWS Batch SubmitJob overrides (loose batch.* params: batch.vcpus, batch.memoryMib, batch.timeoutSeconds, batch.retryAttempts) the server applies at runtime. This is a PURE PLANNING AID: it calls NO terraform, mints NO infra, and records NO deploy-control operation — per-job sizing is a SubmitJob-time concern with zero infra change. A request above 200 GiB ephemeral storage exceeds the Fargate ceiling / tier pool and is reported as an error. gpuCount>0 is surfaced as an advisory note (the default Fargate-Spot substrate has no GPU tiers; GPU needs an opt-in GPU compute-env), not a hard reject. Returns the selected tier token, the gp_job_definition_arns.<tier> output path the server resolves, and the SubmitJob override map.")]
    public Task<OperationResponse> PlanGpJobSizingAsync(
        int vcpus,
        int memoryMib,
        int timeoutSeconds,
        int retryAttempts,
        int ephemeralStorageGib,
        int gpuCount,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        GpResourceProfile profile = new(
            Vcpus: vcpus,
            MemoryMib: memoryMib,
            TimeoutSeconds: timeoutSeconds,
            RetryAttempts: retryAttempts <= 0 ? 1 : retryAttempts,
            EphemeralStorageGib: ephemeralStorageGib <= 0 ? null : ephemeralStorageGib,
            GpuCount: gpuCount);

        GpSizingHint hint = profile.ToSizingHint();

        List<string> findings =
        [
            $"GP job sizing hint (runtime SubmitJob aid; NO terraform, NO infra, NO deploy-control operation).",
            $"Selected job-definition tier: {(hint.TierToken ?? "none (out of range)")}.",
            $"Job-definition ARN output path: {(hint.Tier is { } tier ? GpSubstrateOutputs.JobDefinitionArnForTier(tier) : "n/a")}.",
            "SubmitJob overrides (loose batch.* params the server consumes):",
            .. hint.SubmitJobOverrides.Select(kvp => $"  {kvp.Key} = {kvp.Value}"),
            .. hint.Notes.Select(note => $"Note: {note}")
        ];

        OperationResponse response = new(
            Status: hint.IsValid ? "gp-job-sizing" : "gp-job-sizing-rejected",
            Summary: hint.IsValid
                ? $"GP job sizing: tier `{hint.TierToken}` + SubmitJob overrides (vcpus={profile.Vcpus}, memoryMib={profile.MemoryMib}, timeoutSeconds={profile.TimeoutSeconds}, retryAttempts={profile.RetryAttempts})."
                : $"GP job sizing rejected: {string.Join("; ", hint.Errors)}",
            Findings: findings,
            Actions:
            [
                "Sizing is a pure runtime aid: the server applies the tier + overrides at AWS Batch SubmitJob time with zero infra change.",
                hint.Notes.Count > 0
                    ? "GPU geoprocessing requires an opt-in GPU AWS Batch compute environment; the default Fargate-Spot substrate has no GPU tiers."
                    : "No GPU note; the job fits the default Fargate-Spot substrate tier pool."
            ],
            ValidationChecks:
            [
                hint.IsValid ? "gp-job-sizing-valid:true" : "gp-job-sizing-valid:false",
                "gp-sizing-no-terraform",
                "gp-sizing-no-operation-recorded",
                $"gp-sizing-tier:{hint.TierToken ?? "none"}"
            ],
            Risks:
            [
                .. hint.Errors.Select(error => $"Rejected: {error}"),
                .. hint.Notes.Select(note => $"Advisory: {note}")
            ]);

        return Task.FromResult(response);
    }

    [Description("Compute the runtime SIZING HINT for a single AZURE geoprocessing (GP) job. The Azure equivalent of plan_gp_job_sizing, but SIMPLER: Azure sizes per-POOL and the MVP is a SINGLE pool, so there is NO tier selection and NO vCPU/memory override (those are fixed by the provisioned pool). The hint sets the constant azure.batch.pool_id (resolve from the substrate output gp_pool_id; pass poolId to embed it) plus the narrow per-task overrides Azure Batch supports: azure.batch.task_timeout_minutes, azure.batch.max_task_retry_count, and optionally azure.batch.container_image. PURE PLANNING AID: NO terraform, NO infra, NO deploy-control operation recorded — per-job sizing is a task-add-time concern with zero infra change. task_timeout_minutes<=0 or max_task_retry_count outside 0..100 is an error. Returns the azure.batch.* override map the server applies at task-add time.")]
    public Task<OperationResponse> PlanAzureGpJobSizingAsync(
        int taskTimeoutMinutes,
        int maxTaskRetryCount,
        string containerImage,
        string poolId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        AzureGpResourceProfile profile = new(
            TaskTimeoutMinutes: taskTimeoutMinutes <= 0 ? 60 : taskTimeoutMinutes,
            MaxTaskRetryCount: maxTaskRetryCount < 0 ? 1 : maxTaskRetryCount,
            ContainerImage: string.IsNullOrWhiteSpace(containerImage) ? null : containerImage.Trim());

        AzureGpSizingHint hint = profile.ToSizingHint(string.IsNullOrWhiteSpace(poolId) ? null : poolId.Trim());

        List<string> findings =
        [
            "Azure GP job sizing hint (runtime task-add aid; NO terraform, NO infra, NO deploy-control operation).",
            "Single-pool MVP: no tier selection, no vCPU/memory override (fixed by the provisioned pool).",
            $"Single pool id (azure.batch.pool_id): {(hint.PoolId ?? "unset — resolve from substrate output " + AzureGpSubstrateOutputs.PoolId)}.",
            "Per-task overrides (loose azure.batch.* params the server consumes):",
            .. hint.SubmitTaskOverrides.Select(kvp => $"  {kvp.Key} = {kvp.Value}"),
            .. hint.Notes.Select(note => $"Note: {note}")
        ];

        OperationResponse response = new(
            Status: hint.IsValid ? "azure-gp-job-sizing" : "azure-gp-job-sizing-rejected",
            Summary: hint.IsValid
                ? $"Azure GP job sizing: single pool `{hint.PoolId ?? "(default)"}` + task overrides (task_timeout_minutes={profile.TaskTimeoutMinutes}, max_task_retry_count={profile.MaxTaskRetryCount})."
                : $"Azure GP job sizing rejected: {string.Join("; ", hint.Errors)}",
            Findings: findings,
            Actions:
            [
                "Sizing is a pure runtime aid: the server applies the pool id + task overrides at Azure Batch task-add time with zero infra change.",
                "Resolve azure.batch.pool_id from the substrate output gp_pool_id so the task targets the provisioned single pool."
            ],
            ValidationChecks:
            [
                hint.IsValid ? "azure-gp-job-sizing-valid:true" : "azure-gp-job-sizing-valid:false",
                "azure-gp-sizing-no-terraform",
                "azure-gp-sizing-no-operation-recorded",
                "azure-gp-sizing-single-pool-no-tier"
            ],
            Risks:
            [
                .. hint.Errors.Select(error => $"Rejected: {error}"),
                .. hint.Notes.Select(note => $"Advisory: {note}")
            ]);

        return Task.FromResult(response);
    }

    [Description("View an existing GitOps proposal by its stable operationId as a projection over the honua-server deploy-control operation. Returns the proposal contract with raw evidence references and governed submit/rollback suggestions; never scrapes Git or CI.")]
    public async Task<OperationResponse> GetGitOpsProposalAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        string normalizedOperationId = ValidateOperationId(operationId);
        using BackendJsonResult result = await gateway.GetDeployOperationJsonAsync(normalizedOperationId, cancellationToken);
        JsonElement? root = result.Payload?.RootElement;

        bool found = result.CallResult.IsSuccess && root is not null;
        string status = found ? BridgeStatus.Proposed : BridgeStatus.ContractUnavailable;
        string? serverStatus = root is null ? null : ExtractStatus(root.Value);
        string desiredRevision = (root is null ? null : TryGetString(root.Value, "desiredRevision", "desired_revision", "revision")) ?? "unknown";
        string? currentRevision = root is null ? null : TryGetString(root.Value, "currentRevision", "current_revision");
        string serviceName = (root is null ? null : TryGetParameter(root.Value, "service")) ?? "unknown";
        string action = (root is null ? null : TryGetParameter(root.Value, "action")) ?? "unknown";
        string owner = (root is null ? null : TryGetParameter(root.Value, "owner")) ?? "unknown";
        string[] environments = SplitCsv(root is null ? null : TryGetParameter(root.Value, "environments"));
        string timestamp = Timestamp();

        // Only assert a durable server operation, governed links, and mutating suggestions
        // when the operation was actually found. A not-found / contract-unavailable read
        // keeps OperationId null and offers non-mutating review guidance only, so it never
        // advertises submit/rollback against an operation that does not exist.
        List<EvidenceRef> evidence = [EvidenceFromCall("deploy-operation", result.CallResult)];
        List<WorkflowLink> links = [SelfLink(found ? normalizedOperationId : null)];
        List<SuggestedAction> suggestedActions = [];
        if (found)
        {
            evidence.Add(ServerOperationEvidence(normalizedOperationId));
            links.Add(ServerOperationLink(normalizedOperationId));
            links.Add(GovernedLink("submit", "Submit proposal for execution", normalizedOperationId, "submit"));
            links.Add(GovernedLink("rollback", "Roll back operation", normalizedOperationId, "rollback"));
            suggestedActions.Add(SubmitSuggestion(normalizedOperationId));
            suggestedActions.Add(RollbackSuggestion(normalizedOperationId));
        }
        suggestedActions.Add(ReviewEvidenceSuggestion(found ? normalizedOperationId : null));

        bool targetsProd = environments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        bool approvalRequired = EffectivePolicy.ApprovalMode != ApprovalMode.DirectAllowed || targetsProd;
        string proposalStatus = found
            ? MapProposalLifecycle(serverStatus, approvalRequired)
            : ProposalLifecycle.Unknown;
        GitOpsProposalBridge proposal = new(
            ProposalId: normalizedOperationId,
            OperationId: found ? normalizedOperationId : null,
            IdempotencyKey: "server-managed",
            Status: status,
            Service: serviceName,
            TargetEnvironments: environments,
            DesiredRevision: desiredRevision,
            CurrentRevision: currentRevision,
            RequestedAction: action,
            EffectiveAction: "propose",
            Owner: owner,
            ApprovalRequired: approvalRequired,
            WorkflowLinks: links,
            Evidence: evidence,
            SuggestedActions: suggestedActions,
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            Kind: OperationKind,
            Requester: owner,
            Agent: AgentIdentity,
            ProposalStatus: proposalStatus,
            Plan: BuildProposalPlan(
                serviceName,
                environments,
                desiredRevision,
                action,
                approvalRequired,
                targetsProd,
                blockingReasons: found
                    ? []
                    : [$"No durable deploy-control operation found for `{normalizedOperationId}`."]));

        return BuildProposalResponse(
            proposal,
            [ToStep("deploy-operation-status", result.CallResult, mutatesState: false)],
            blockingReason: found ? null : $"No durable deploy-control operation found for `{normalizedOperationId}`.");
    }

    [Description("Record an auditable approve or reject decision against a GitOps proposal by its stable operationId. The decision captures the deciding actor and a free-form reason (consistent with the honua-server decision audit), and projects the proposal with its canonical lifecycle moved toward Submitted (approve) or Rejected (reject). The bridge records the decision only; it never submits, executes, or rolls back. An approve decision surfaces the governed submit suggestion that still requires an explicit governed submit. Returns a blocked projection if the operation cannot be read.")]
    public async Task<OperationResponse> RecordProposalDecisionAsync(
        string operationId,
        string decision,
        string actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        string normalizedOperationId = ValidateOperationId(operationId);
        string normalizedDecision = NormalizeDecision(decision);
        string normalizedActor = DeploymentInputs.SanitizeFreeText(actor, "unassigned");
        string normalizedReason = DeploymentInputs.SanitizeFreeText(reason, "not provided");
        string timestamp = Timestamp();

        using BackendJsonResult result = await gateway.GetDeployOperationJsonAsync(normalizedOperationId, cancellationToken);
        JsonElement? root = result.Payload?.RootElement;
        bool found = result.CallResult.IsSuccess && root is not null;
        string? serverStatus = root is null ? null : ExtractStatus(root.Value);
        string desiredRevision = (root is null ? null : TryGetString(root.Value, "desiredRevision", "desired_revision", "revision")) ?? "unknown";
        string? currentRevision = root is null ? null : TryGetString(root.Value, "currentRevision", "current_revision");
        string serviceName = (root is null ? null : TryGetParameter(root.Value, "service")) ?? "unknown";
        string action = (root is null ? null : TryGetParameter(root.Value, "action")) ?? "unknown";
        string owner = (root is null ? null : TryGetParameter(root.Value, "owner")) ?? "unknown";
        string[] environments = SplitCsv(root is null ? null : TryGetParameter(root.Value, "environments"));
        bool targetsProd = environments.Contains("prod", StringComparer.OrdinalIgnoreCase);
        bool approvalRequired = EffectivePolicy.ApprovalMode != ApprovalMode.DirectAllowed || targetsProd;

        bool isApprove = normalizedDecision == ProposalDecisionKind.Approve;
        string resultingStatus = !found
            ? ProposalLifecycle.Unknown
            : isApprove ? ProposalLifecycle.Submitted : ProposalLifecycle.Rejected;
        string governedAction = isApprove ? "submit" : "none";
        ProposalDecision decisionRecord = new(
            Decision: normalizedDecision,
            Actor: normalizedActor,
            Reason: normalizedReason,
            DecidedAt: timestamp,
            ResultingStatus: resultingStatus,
            GovernedAction: governedAction);

        string bridgeStatus = found ? BridgeStatus.Proposed : BridgeStatus.ContractUnavailable;
        List<EvidenceRef> evidence = [EvidenceFromCall("deploy-operation", result.CallResult)];
        List<WorkflowLink> links = [SelfLink(found ? normalizedOperationId : null)];
        List<SuggestedAction> suggestedActions = [];
        if (found)
        {
            evidence.Add(ServerOperationEvidence(normalizedOperationId));
            links.Add(ServerOperationLink(normalizedOperationId));
            // An approve decision authorizes the governed submit; a reject decision never
            // surfaces a mutating action. Either way the bridge records only — it never runs it.
            if (isApprove)
            {
                links.Add(GovernedLink("submit", "Submit proposal for execution", normalizedOperationId, "submit"));
                suggestedActions.Add(SubmitSuggestion(normalizedOperationId));
            }
        }
        suggestedActions.Add(ReviewEvidenceSuggestion(found ? normalizedOperationId : null));

        GitOpsProposalBridge proposal = new(
            ProposalId: normalizedOperationId,
            OperationId: found ? normalizedOperationId : null,
            IdempotencyKey: "server-managed",
            Status: bridgeStatus,
            Service: serviceName,
            TargetEnvironments: environments,
            DesiredRevision: desiredRevision,
            CurrentRevision: currentRevision,
            RequestedAction: action,
            EffectiveAction: "propose",
            Owner: owner,
            ApprovalRequired: approvalRequired,
            WorkflowLinks: links,
            Evidence: evidence,
            SuggestedActions: suggestedActions,
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            Kind: OperationKind,
            Requester: owner,
            Agent: AgentIdentity,
            ProposalStatus: resultingStatus,
            Plan: BuildProposalPlan(
                serviceName,
                environments,
                desiredRevision,
                action,
                approvalRequired,
                targetsProd,
                blockingReasons: found
                    ? []
                    : [$"No durable deploy-control operation found for `{normalizedOperationId}`."]),
            Decision: decisionRecord);

        ConsoleBridgeProjection projection = new("gitops-proposal", Proposal: proposal);

        List<string> findings =
        [
            $"Operation id: {proposal.OperationId ?? "none (blocked)"}",
            $"Decision: {decisionRecord.Decision}",
            $"Decision actor: {decisionRecord.Actor}",
            $"Decision reason: {decisionRecord.Reason}",
            $"Decided at: {decisionRecord.DecidedAt}",
            $"Resulting lifecycle status: {decisionRecord.ResultingStatus}",
            $"Governed action authorized: {decisionRecord.GovernedAction}"
        ];
        if (!found)
        {
            findings.Add($"Blocked: No durable deploy-control operation found for `{normalizedOperationId}`.");
        }

        return new OperationResponse(
            Status: bridgeStatus,
            Summary: $"Recorded {decisionRecord.Decision} decision for proposal `{normalizedOperationId}` by {decisionRecord.Actor}.",
            Findings: findings,
            Actions:
            [
                "Decision is recorded for audit only; the bridge never auto-submits or rolls back.",
                isApprove
                    ? "Approval authorizes a separate governed submit through the deploy-control approval path."
                    : "Rejection records the terminal decision; no governed action is authorized.",
                .. suggestedActions.Select(suggested =>
                    $"Suggested action `{suggested.Id}`: {suggested.Title} (requiresApproval={suggested.RequiresApproval}, mutatesState={suggested.MutatesState}).")
            ],
            ValidationChecks:
            [
                "decision-audit-actor-and-reason",
                "decision-records-only-no-auto-execute",
                "lifecycle-aligned-with-server-status"
            ],
            Risks:
            [
                "A recorded approval still requires an explicit governed submit; the bridge never executes it.",
                "Decision reflects proposal scope at decision time and can drift if submitted much later."
            ],
            BackendSteps: [ToStep("deploy-operation-read", result.CallResult, mutatesState: false)],
            ConsoleBridge: projection);
    }

    [Description("Get the unified DevOps operation status for a stable operationId. Projects the honua-server deploy-control workflow status into proposal, PR, CI, promotion, smoke, SLO-watch, rollback-readiness, and rollback-execution sections that all share the same operationId. PR/CI sections are marked evidence-missing rather than scraping GitHub or CI.")]
    public async Task<OperationResponse> GetDevOpsOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        string normalizedOperationId = ValidateOperationId(operationId);
        using BackendJsonResult result = await gateway.GetDeployOperationJsonAsync(normalizedOperationId, cancellationToken);
        JsonElement? root = result.Payload?.RootElement;
        string? serverStatus = root is null ? null : ExtractStatus(root.Value);
        string? providerOperationId = root is null
            ? null
            : TryGetString(root.Value, "providerOperationId", "provider_operation_id", "externalId");

        DevOpsOperationStatus status = BuildOperationStatus(
            normalizedOperationId,
            serverStatus,
            providerOperationId,
            result.CallResult);

        ConsoleBridgeProjection projection = new("operation-status", OperationStatus: status);

        List<string> findings =
        [
            $"Operation id: {normalizedOperationId}",
            $"Server status: {serverStatus ?? "unavailable"}",
            $"Bridge status: {status.Status} (phase {status.Phase})",
            $"Provider operation id: {providerOperationId ?? "unset"}",
            $"Deploy-control endpoint: {result.CallResult.Endpoint}",
            $"Backend result: {result.CallResult.Detail}"
        ];
        findings.AddRange(status.BlockingReasons.Select(reason => $"Blocking: {reason}"));

        return new OperationResponse(
            Status: status.Status,
            Summary: $"DevOps operation `{normalizedOperationId}` status: {status.Status}.",
            Findings: findings,
            Actions:
            [
                "Operation id is stable across proposal, PR, CI, promotion, SLO watch, and rollback sections.",
                "Route any rollback or submit through the governed deploy-control path with explicit approval.",
                .. status.Warnings.Select(warning => $"Warning: {warning}")
            ],
            ValidationChecks:
            [
                "operation-id-stable",
                "pr-ci-evidence-missing-not-scraped",
                "status-read-bounded"
            ],
            Risks:
            [
                "Status reflects deploy-control at read time and can change on the next poll.",
                "PR/CI evidence is owned by the honua-server #59/#58 child ticket and is not inferred here."
            ],
            BackendSteps: [ToStep("deploy-operation-status", result.CallResult, mutatesState: false)],
            ConsoleBridge: projection);
    }

    [Description("Build an advisory AI DevOps brief: affected resources, raw evidence references, suggested actions (with requiresApproval/mutatesState flags), confidence, owner, status, and target workflow links. The brief is advisory only with auto-apply disabled; mutating suggestions are surfaced but never executed and require an explicit governed submit/rollback. Pass an operationId to link the brief to a durable workflow.")]
    public Task<OperationResponse> BuildAiDevOpsBriefAsync(
        string service,
        string environment,
        string title,
        string summary,
        string recommendedAction,
        string evidenceReference,
        string operationId,
        string confidence,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        string normalizedService = DeploymentInputs.ValidateServiceName(service);
        string normalizedEnvironment = DeploymentInputs.SanitizeFreeText(environment, "unspecified");
        string normalizedTitle = DeploymentInputs.SanitizeFreeText(title, "AI DevOps brief");
        string normalizedSummary = DeploymentInputs.SanitizeFreeText(summary, "not provided");
        string normalizedRecommendedAction = DeploymentInputs.SanitizeFreeText(recommendedAction, "review and decide");
        string normalizedEvidenceReference = DeploymentInputs.SanitizeFreeText(evidenceReference, string.Empty);
        string normalizedConfidence = NormalizeConfidence(confidence);
        string? linkedOperationId = string.IsNullOrWhiteSpace(operationId) ? null : ValidateOperationId(operationId);
        string timestamp = Timestamp();

        List<AffectedResource> affectedResources =
        [
            new AffectedResource("service", normalizedService, normalizedEnvironment, normalizedSummary),
            new AffectedResource("environment", normalizedEnvironment, normalizedEnvironment, null)
        ];

        List<EvidenceRef> evidence = [];
        if (!string.IsNullOrWhiteSpace(normalizedEvidenceReference))
        {
            evidence.Add(new EvidenceRef(
                Type: "ai-evidence",
                Source: "agent",
                RawRef: normalizedEvidenceReference,
                Url: null,
                Summary: "Raw evidence reference supplied with the advisory brief.",
                CapturedAt: timestamp,
                Sensitivity: EvidenceSensitivity.Internal));
        }

        if (linkedOperationId is not null)
        {
            evidence.Add(ServerOperationEvidence(linkedOperationId));
        }

        if (evidence.Count == 0)
        {
            // Keep the contract honest: surface the gap instead of an empty evidence list.
            evidence.Add(new EvidenceRef(
                Type: BridgeStatus.EvidenceMissing,
                Source: "agent",
                RawRef: null,
                Url: null,
                Summary: "No raw evidence reference or operationId was supplied for this brief.",
                CapturedAt: timestamp,
                Sensitivity: EvidenceSensitivity.Internal));
        }

        List<WorkflowLink> links = [SelfLink(linkedOperationId)];
        if (linkedOperationId is not null)
        {
            links.Add(ServerOperationLink(linkedOperationId));
        }

        List<SuggestedAction> suggestedActions =
        [
            new SuggestedAction(
                Id: "review-recommendation",
                Title: "Review recommended action",
                Description: normalizedRecommendedAction,
                RequiresApproval: false,
                MutatesState: false,
                TargetOperationId: linkedOperationId,
                WorkflowLink: SelfLink(linkedOperationId),
                Kind: "advisory")
        ];
        if (linkedOperationId is not null)
        {
            suggestedActions.Add(SubmitSuggestion(linkedOperationId));
            suggestedActions.Add(RollbackSuggestion(linkedOperationId));
        }

        AiDevOpsBrief brief = new(
            BriefId: linkedOperationId is null
                ? $"brief:{ShortHash($"{normalizedService}:{normalizedEnvironment}:{normalizedTitle}:{timestamp}")}"
                : $"brief:{linkedOperationId}",
            OperationId: linkedOperationId,
            Title: normalizedTitle,
            Summary: normalizedSummary,
            AffectedResources: affectedResources,
            Evidence: evidence,
            SuggestedActions: suggestedActions,
            Confidence: normalizedConfidence,
            Owner: "unassigned",
            Status: BridgeStatus.Advisory,
            WorkflowLinks: links,
            CreatedAt: timestamp);

        ConsoleBridgeProjection projection = new("ai-devops-brief", Brief: brief);

        OperationResponse response = new(
            Status: BridgeStatus.Advisory,
            Summary: $"Advisory AI DevOps brief for `{normalizedService}` ({normalizedEnvironment}).",
            Findings:
            [
                $"Title: {normalizedTitle}",
                $"Confidence: {normalizedConfidence}",
                $"Linked operation id: {linkedOperationId ?? "none"}",
                $"Evidence references: {evidence.Count}",
                $"Affected resources: {string.Join(", ", affectedResources.Select(resource => $"{resource.Kind}:{resource.Name}"))}"
            ],
            Actions:
            [
                "Brief is advisory only; auto-apply is disabled.",
                "Mutating suggestions require an explicit governed submit/rollback with approval.",
                $"Recommended action: {normalizedRecommendedAction}"
            ],
            ValidationChecks:
            [
                "advisory-brief-no-auto-apply",
                "evidence-references-present",
                "mutating-suggestions-require-approval"
            ],
            Risks:
            [
                "Advisory confidence is a heuristic and does not replace operator judgment.",
                "Acting on the brief still requires governed approval and validated evidence."
            ],
            ConsoleBridge: projection);

        return Task.FromResult(response);
    }

    internal static string BuildProposalIdempotencyKey(
        string targetId,
        string service,
        IReadOnlyList<string> environments,
        string revision,
        string action)
    {
        string envs = string.Join(
            "-",
            environments.Select(environment => environment.ToLowerInvariant()).OrderBy(environment => environment, StringComparer.Ordinal));
        string descriptor = $"{targetId}:{service}:{envs}:{revision}:{action}";
        string key = $"honua-devops:proposal:{descriptor}";
        if (key.Length <= MaxReadableKeyLength)
        {
            return key;
        }

        // Collapse the variable scope into a stable short hash to bound key length while
        // keeping the target id readable.
        return $"honua-devops:proposal:{targetId}:{ShortHash(descriptor)}";
    }

    private OperationResponse BuildProposalResponse(
        GitOpsProposalBridge proposal,
        IReadOnlyList<OperationBackendStep>? backendSteps,
        string? blockingReason)
    {
        ConsoleBridgeProjection projection = new("gitops-proposal", Proposal: proposal);

        List<string> findings =
        [
            $"Proposal id: {proposal.ProposalId}",
            $"Operation id: {proposal.OperationId ?? "none (blocked)"}",
            $"Idempotency key: {proposal.IdempotencyKey}",
            $"Status: {proposal.Status}",
            $"Service: {proposal.Service} -> {string.Join(", ", proposal.TargetEnvironments)} @ {proposal.DesiredRevision}",
            $"Requested action: {proposal.RequestedAction}; effective action: {proposal.EffectiveAction}",
            $"Approval required: {proposal.ApprovalRequired}",
            $"Owner: {proposal.Owner}",
            $"Evidence references: {proposal.Evidence.Count}"
        ];
        if (blockingReason is not null)
        {
            findings.Add($"Blocked: {blockingReason}");
        }

        return new OperationResponse(
            Status: proposal.Status,
            Summary: $"GitOps proposal for `{proposal.Service}` -> {string.Join(", ", proposal.TargetEnvironments)} @ {proposal.DesiredRevision} ({proposal.RequestedAction}).",
            Findings: findings,
            Actions:
            [
                "Proposal recorded with submitImmediately=false; execution requires a separate governed submit.",
                .. proposal.SuggestedActions.Select(action =>
                    $"Suggested action `{action.Id}`: {action.Title} (requiresApproval={action.RequiresApproval}, mutatesState={action.MutatesState}).")
            ],
            ValidationChecks:
            [
                "proposal-no-auto-execute",
                "operation-id-stable",
                "idempotency-key-scope-derived"
            ],
            Risks:
            [
                proposal.ApprovalRequired
                    ? "Proposal requires governed approval before any execution."
                    : "Direct-allowed policy still requires an explicit submit; the bridge never auto-submits.",
                "Proposal scope can drift from reality if submitted long after creation."
            ],
            BackendSteps: backendSteps,
            ConsoleBridge: projection);
    }

    private DevOpsOperationStatus BuildOperationStatus(
        string operationId,
        string? serverStatus,
        string? providerOperationId,
        BackendCallResult call)
    {
        (string status, string phase) = MapServerStatus(serverStatus);
        if (!call.IsSuccess && serverStatus is null)
        {
            status = BridgeStatus.Unknown;
            phase = BridgeStatus.Unknown;
        }

        string timestamp = Timestamp();
        EvidenceRef serverEvidence = ServerOperationEvidence(operationId);
        EvidenceRef callEvidence = EvidenceFromCall("deploy-operation", call);
        IReadOnlyList<EvidenceRef> operationEvidence = [serverEvidence];

        WorkflowStageStatus proposalStage = Stage("proposal", ProposalStageStatus(status), $"Proposal recorded for operation {operationId}.", operationEvidence);
        WorkflowStageStatus prStage = EvidenceMissingStage("pr", "Pull-request evidence is owned by honua-server #59/#58 and is not scraped from GitHub.");
        WorkflowStageStatus ciStage = EvidenceMissingStage("ci", "CI evidence is owned by honua-server #59/#58 and is not scraped from CI logs.");
        WorkflowStageStatus promotionStage = Stage("promotion", PromotionStageStatus(status), $"Promotion derived from deploy-control status `{serverStatus ?? "unavailable"}`.", operationEvidence);
        WorkflowStageStatus smokeStage = EvidenceMissingStage("smoke", "Smoke evidence is supplied by release orchestration or server and is not inferred here.");
        WorkflowStageStatus sloStage = EvidenceMissingStage("slo-watch", "SLO-watch evidence is supplied by server telemetry and is not inferred here.");
        WorkflowStageStatus rollbackReadiness = Stage("rollback-readiness", RollbackReadinessStatus(status), "Rollback readiness derived from deploy-control status.", operationEvidence);
        WorkflowStageStatus rollbackExecution = Stage("rollback-execution", RollbackExecutionStatus(status), "Rollback execution derived from deploy-control status.", operationEvidence);

        List<string> blocking = [];
        if (status is "failed")
        {
            blocking.Add("Server reported operation failure; review backend evidence before retry or rollback.");
        }
        else if (status is "manual-intervention-required")
        {
            blocking.Add("Server requires manual intervention before the operation can proceed.");
        }
        else if (status is BridgeStatus.Unknown)
        {
            blocking.Add("Deploy-control status could not be read; see backend evidence.");
        }

        List<string> warnings =
        [
            "PR and CI evidence are not provided by deploy-control; honua-server #59/#58 owns durable PR/CI links."
        ];

        return new DevOpsOperationStatus(
            OperationId: operationId,
            Kind: OperationKind,
            Status: status,
            Phase: phase,
            ProviderOperationId: providerOperationId,
            Proposal: proposalStage,
            Pr: prStage,
            Ci: ciStage,
            Promotion: promotionStage,
            Smoke: smokeStage,
            SloWatch: sloStage,
            RollbackReadiness: rollbackReadiness,
            RollbackExecution: rollbackExecution,
            Evidence: [serverEvidence, callEvidence],
            BlockingReasons: blocking,
            Warnings: warnings,
            LastUpdated: timestamp);
    }

    private static (string Status, string Phase) MapServerStatus(string? serverStatus)
    {
        return (serverStatus ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "planned" => ("planned", "plan"),
            "awaitingapproval" or "awaiting-approval" => ("awaiting-approval", "approval"),
            "submitted" => ("submitted", "execution"),
            "reconciling" => ("reconciling", "execution"),
            "succeeded" => ("succeeded", "complete"),
            "failed" => ("failed", "complete"),
            "rollbackrequested" or "rollback-requested" => ("rollback-requested", "rollback"),
            "rolledback" or "rolled-back" => ("rolled-back", "rollback"),
            "manualinterventionrequired" or "manual-intervention-required" => ("manual-intervention-required", "blocked"),
            _ => (BridgeStatus.Unknown, BridgeStatus.Unknown)
        };
    }

    // Maps a honua-server WorkflowOperationStatus (any casing, hyphenated or PascalCase) onto
    // the canonical ProposalLifecycle value Console aggregates across both proposal sources
    // (issue #78). The mapping is 1:1 with the server enum; the only synthesis is the
    // unreadable case, where a recorded proposal is reported as AwaitingApproval when approval
    // is required (the safe default the deploy target enforces) or Planned otherwise, so a
    // freshly recorded proposal still enters the lifecycle rather than reporting unknown.
    // Rejected has no distinct server enum member and is only produced by a recorded reject
    // decision, never by reading a server status.
    internal static string MapProposalLifecycle(string? serverStatus, bool approvalRequired)
    {
        return (serverStatus ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "planned" => ProposalLifecycle.Planned,
            "awaitingapproval" or "awaiting-approval" => ProposalLifecycle.AwaitingApproval,
            "submitted" => ProposalLifecycle.Submitted,
            "reconciling" => ProposalLifecycle.Reconciling,
            "succeeded" => ProposalLifecycle.Succeeded,
            "failed" => ProposalLifecycle.Failed,
            "rollbackrequested" or "rollback-requested" => ProposalLifecycle.RollbackRequested,
            "rolledback" or "rolled-back" => ProposalLifecycle.RolledBack,
            "manualinterventionrequired" or "manual-intervention-required" => ProposalLifecycle.ManualInterventionRequired,
            _ => approvalRequired ? ProposalLifecycle.AwaitingApproval : ProposalLifecycle.Planned
        };
    }

    // Builds the provider-neutral proposal plan (diff + dry-run + risk + blocking reasons).
    // Proposals are always recorded with submitImmediately=false, so DryRun is always true: the
    // create call records a durable operation but executes nothing. Risk is a coarse, derived
    // classification (prod-targeting destructive actions are high; prod-targeting syncs are
    // elevated; everything else is standard) so Console can colour the approval surface without
    // re-deriving it from prose.
    private static ProposalPlan BuildProposalPlan(
        string service,
        IReadOnlyList<string> environments,
        string revision,
        string action,
        bool approvalRequired,
        bool targetsProd,
        IReadOnlyList<string> blockingReasons)
    {
        string envs = environments.Count == 0 ? "(none)" : string.Join(", ", environments);
        string diffSummary = $"{action} {service} -> {envs} @ {revision}";
        bool destructive = action is "rollback" or "delete" or "destroy";
        string risk = targetsProd
            ? destructive ? "high" : "elevated"
            : destructive ? "elevated" : "standard";
        List<string> warnings = [];
        if (targetsProd)
        {
            warnings.Add("Targets a production environment; governed approval is required before submission.");
        }

        return new ProposalPlan(
            DiffSummary: diffSummary,
            DryRun: true,
            RequiresApproval: approvalRequired,
            Risk: risk,
            BlockingReasons: blockingReasons,
            Warnings: warnings);
    }

    private static string NormalizeDecision(string? value)
    {
        return DeploymentInputs.Normalize(value, string.Empty).ToLowerInvariant() switch
        {
            "approve" or "approved" or "accept" => ProposalDecisionKind.Approve,
            "reject" or "rejected" or "deny" or "decline" => ProposalDecisionKind.Reject,
            _ => throw new InvalidOperationException("Decision must be 'approve' or 'reject'.")
        };
    }

    private static string ProposalStageStatus(string status) => status switch
    {
        "planned" or "awaiting-approval" => "passed",
        BridgeStatus.Unknown => BridgeStatus.EvidenceMissing,
        _ => "passed"
    };

    private static string PromotionStageStatus(string status) => status switch
    {
        "planned" or "awaiting-approval" => "pending",
        "submitted" or "reconciling" => "in-progress",
        "succeeded" => "passed",
        "failed" or "manual-intervention-required" => "failed",
        "rollback-requested" or "rolled-back" => "superseded",
        _ => BridgeStatus.EvidenceMissing
    };

    private static string RollbackReadinessStatus(string status) => status switch
    {
        "planned" or "awaiting-approval" => "not-ready",
        "submitted" or "reconciling" or "succeeded" or "failed" or "manual-intervention-required" => "ready",
        "rollback-requested" => "in-progress",
        "rolled-back" => "not-applicable",
        _ => BridgeStatus.EvidenceMissing
    };

    private static string RollbackExecutionStatus(string status) => status switch
    {
        "rollback-requested" => "in-progress",
        "rolled-back" => "passed",
        _ => "not-applicable"
    };

    private static WorkflowStageStatus Stage(string stage, string status, string detail, IReadOnlyList<EvidenceRef> evidence)
        => new(stage, status, detail, evidence);

    private static WorkflowStageStatus EvidenceMissingStage(string stage, string detail)
        => new(stage, BridgeStatus.EvidenceMissing, detail, []);

    private WorkflowLink SelfLink(string? operationId)
    {
        string? href = operationId is null ? null : ComposeUrl(Configuration.ConsoleBaseUri, $"operations/{Uri.EscapeDataString(operationId)}");
        return new WorkflowLink("self", "Open in Console", href, href is not null);
    }

    private WorkflowLink ServerOperationLink(string operationId)
    {
        string? href = ComposeUrl(Configuration.HonuaApiBaseUri, $"{Configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}");
        return new WorkflowLink("server-operation", "Deploy-control operation", href, href is not null);
    }

    private WorkflowLink GovernedLink(string rel, string label, string operationId, string verb)
    {
        string? href = ComposeUrl(Configuration.HonuaApiBaseUri, $"{Configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}/{verb}");
        return new WorkflowLink(rel, label, href, href is not null);
    }

    private EvidenceRef ServerOperationEvidence(string operationId)
    {
        string? href = ComposeUrl(Configuration.HonuaApiBaseUri, $"{Configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}");
        return new EvidenceRef(
            Type: "deploy-operation",
            Source: "honua-server",
            RawRef: $"deploy-operation:{operationId}",
            Url: href,
            Summary: "honua-server deploy-control operation record.",
            CapturedAt: Timestamp(),
            Sensitivity: EvidenceSensitivity.Internal);
    }

    private static EvidenceRef EvidenceFromCall(string type, BackendCallResult call)
        => new(
            Type: type,
            Source: "honua-server",
            RawRef: call.Endpoint,
            Url: call.Endpoint,
            // PayloadPreview is already scrubbed by the transport before it reaches here.
            Summary: $"{call.Detail} :: {call.PayloadPreview}",
            CapturedAt: Timestamp(),
            Sensitivity: EvidenceSensitivity.Internal);

    private SuggestedAction SubmitSuggestion(string operationId)
        => new(
            Id: "submit-operation",
            Title: "Submit operation for execution",
            Description: "Submit the durable deploy-control operation for execution. Requires explicit governed approval.",
            RequiresApproval: true,
            MutatesState: true,
            TargetOperationId: operationId,
            WorkflowLink: GovernedLink("submit", "Submit proposal for execution", operationId, "submit"),
            Kind: "governed-submit");

    private SuggestedAction RollbackSuggestion(string operationId)
        => new(
            Id: "rollback-operation",
            Title: "Roll back operation",
            Description: "Roll back the deploy-control operation to the prior healthy revision. Requires explicit governed approval.",
            RequiresApproval: true,
            MutatesState: true,
            TargetOperationId: operationId,
            WorkflowLink: GovernedLink("rollback", "Roll back operation", operationId, "rollback"),
            Kind: "governed-rollback");

    private SuggestedAction ReviewEvidenceSuggestion(string? operationId)
        => new(
            Id: "review-evidence",
            Title: "Review proposal evidence",
            Description: "Review preflight, plan, and deploy-control evidence before approving execution.",
            RequiresApproval: false,
            MutatesState: false,
            TargetOperationId: operationId,
            WorkflowLink: SelfLink(operationId),
            Kind: "advisory");

    private SuggestedAction ConfigureTargetSuggestion()
        => new(
            Id: "configure-deploy-target",
            Title: "Configure deploy target",
            Description: "Set HONUA_DEVOPS_DEPLOY_TARGET_ID so a durable server operation can be created for the proposal.",
            RequiresApproval: false,
            MutatesState: false,
            TargetOperationId: null,
            WorkflowLink: null,
            Kind: "advisory");

    private static OperationBackendStep ToStep(string name, BackendCallResult result, bool mutatesState)
        => new(
            Name: name,
            Endpoint: result.Endpoint,
            Success: result.IsSuccess,
            Detail: result.Detail,
            PayloadPreview: result.PayloadPreview,
            MutatesState: mutatesState);

    private static string ValidateOperationId(string value)
    {
        string operationId = DeploymentInputs.Normalize(value, string.Empty);
        if (operationId.Length is < 1 or > 200)
        {
            throw new InvalidOperationException("Operation id must be 1-200 characters.");
        }

        if (operationId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("Operation id must not contain whitespace or control characters.");
        }

        return operationId;
    }

    private static string NormalizeConfidence(string? value)
    {
        return DeploymentInputs.Normalize(value, "medium").ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" or "med" => "medium",
            "high" => "high",
            _ => "medium"
        };
    }

    private static string[] SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    // Tolerant, case-insensitive string lookup over the server response object plus a
    // shallow descent into common nesting (response envelopes and the deploy-control
    // "target" sub-object; see NestedObjectKeys). Tolerant parsing keeps the bridge
    // resilient to envelope/casing drift across deploy-control responses.
    private static string? TryGetString(JsonElement element, params string[] names)
    {
        string? direct = ReadString(element, names);
        if (direct is not null)
        {
            return direct;
        }

        foreach (string envelope in NestedObjectKeys)
        {
            if (TryGetObject(element, envelope, out JsonElement nested))
            {
                string? value = ReadString(nested, names);
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    string? value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? ExtractOperationId(JsonElement root)
        => TryGetString(root, "operationId", "operation_id", "id");

    private static string? ExtractStatus(JsonElement root)
        => TryGetString(root, "status", "state", "workflowStatus");

    private static string? TryGetParameter(JsonElement root, string name)
    {
        if (TryGetObject(root, "parameters", out JsonElement parameters))
        {
            string? value = ReadString(parameters, [name]);
            if (value is not null)
            {
                return value;
            }
        }

        // honua-server DeployOperationResponse nests the create-request parameters under
        // target.parameters (merged onto the deploy target), so service/action/owner/
        // environments echoed back on a read live there, not at the root.
        if (TryGetObject(root, "target", out JsonElement target)
            && TryGetObject(target, "parameters", out JsonElement targetParameters))
        {
            string? value = ReadString(targetParameters, [name]);
            if (value is not null)
            {
                return value;
            }
        }

        return ReadString(root, [name]);
    }

    // Nested objects the tolerant lookup descends into after a direct root read:
    // the "operation"/"data"/"result" response envelopes and the honua-server
    // deploy-control "target" sub-object, which carries desiredRevision/currentRevision
    // (and parameters) on a DeployOperationResponse.
    private static readonly string[] NestedObjectKeys = ["operation", "data", "result", "target"];

    private static GpCpuArchitecture ParseGpArchitecture(string value)
    {
        string normalized = DeploymentInputs.Normalize(value, "x86_64").Trim().ToLowerInvariant();
        return normalized switch
        {
            "x86_64" or "x86-64" or "x86" or "amd64" => GpCpuArchitecture.X86_64,
            "arm64" or "arm" or "aarch64" or "graviton" => GpCpuArchitecture.Arm64,
            _ => throw new InvalidOperationException(
                $"Invalid GP CPU architecture `{value}`. Allowed values: x86_64, arm64.")
        };
    }

    private static IReadOnlyList<GpJobDefinitionTier> ParseGpTiers(string value)
    {
        // Empty/blank => the full s/m/l/xl pool (GpSubstrateConfig.DefaultTiers).
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<GpJobDefinitionTier> tiers = [];
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            GpJobDefinitionTier tier = token.ToLowerInvariant() switch
            {
                "s" or "small" => GpJobDefinitionTier.S,
                "m" or "medium" => GpJobDefinitionTier.M,
                "l" or "large" => GpJobDefinitionTier.L,
                "xl" or "extra-large" or "xlarge" => GpJobDefinitionTier.Xl,
                _ => throw new InvalidOperationException(
                    $"Invalid GP job-definition tier `{token}`. Allowed values: s, m, l, xl.")
            };
            if (!tiers.Contains(tier))
            {
                tiers.Add(tier);
            }
        }

        return tiers;
    }

    private static string GpSubstrateDescriptor(IReadOnlyList<GpSubstrateVar> vars)
    {
        // Compact, deterministic descriptor of the substrate knobs for the proposal summary.
        return string.Join(
            ", ",
            vars
                .Where(v => v.Name is GpSubstrateConfig.ImageVar
                    or GpSubstrateConfig.CpuArchitectureVar
                    or GpSubstrateConfig.MaxVcpusVar
                    or GpSubstrateConfig.TiersVar)
                .Select(v => $"{v.Name}={(string.IsNullOrEmpty(v.Value) ? "(lambda-default)" : v.Value)}"));
    }

    // Build the plan-first GP substrate step plan WITHOUT any backend call, so the terraform
    // commands shown are exactly what a gated execute would run (plan in plan mode, apply when
    // gated) against the per-env substrate stack.
    private RuntimeAdapterWorkflow BuildGpWorkflow(GpRuntimeAdapter adapter)
    {
        RuntimeAdapterRequest request = new(
            Service: "geoprocessing",
            Environments: runtime.AllowedEnvironments,
            Revision: "HEAD",
            Action: "apply",
            ChangeSummary: "gp per-env substrate provision",
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            TerraformLocalPath: runtime.TerraformLocalPath,
            // PLAN-FIRST: DryRun is true unless the agent is in execute mode, so the
            // apply-infra step degrades to terraform plan and mutates nothing by default.
            DryRun: runtime.ExecutionMode != ExecutionMode.Execute,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        return adapter.BuildWorkflow(request);
    }

    private OperationResponse EnrichGpProposal(
        OperationResponse proposal,
        GpRuntimeAdapter adapter,
        IReadOnlyList<string> substrateFindings)
    {
        RuntimeAdapterWorkflow workflow = BuildGpWorkflow(adapter);
        bool planFirst = runtime.ExecutionMode != ExecutionMode.Execute;

        List<string> findings =
        [
            .. proposal.Findings,
            $"GP runtime adapter target: {adapter.Capability.Target} ({adapter.Capability.Family}).",
            $"GP substrate provision mode: {(planFirst ? "plan-first (terraform plan only; provisions nothing)" : "execute (gated terraform apply)")}.",
            .. substrateFindings,
            $"Validate step: {workflow.Validate.Summary}",
            $"Plan-infra command: {workflow.PlanInfrastructure.SuggestedCommands.FirstOrDefault() ?? "(none)"}",
            $"Apply-infra step: {workflow.ApplyInfrastructure.Summary}"
        ];

        List<string> checks =
        [
            .. proposal.ValidationChecks,
            .. workflow.Validate.ValidationChecks,
            .. workflow.PlanInfrastructure.ValidationChecks,
            planFirst ? "gp-apply-mode:plan-only" : "gp-apply-mode:write-enabled-gated"
        ];

        List<string> risks =
        [
            .. proposal.Risks,
            .. workflow.PlanInfrastructure.Risks,
            .. workflow.ApplyInfrastructure.Risks
        ];

        return proposal with
        {
            Summary = $"GP per-env substrate provision proposal ({adapter.Capability.Target}) for `{adapter.Substrate.WorkloadId}` — {(planFirst ? "plan-first, advisory" : "gated execute")}.",
            Findings = findings,
            ValidationChecks = checks,
            Risks = risks
        };
    }

    private static string AzureGpSubstrateDescriptor(AzureGpSubstrateConfig substrate)
    {
        // Compact, deterministic descriptor of the Azure substrate knobs for the proposal summary.
        // Includes the image/ACR even though they are not terraform passthrough vars of the
        // azure-cert stack (the module owns the structured image / derives the ACR name): they
        // still describe the substrate the proposal stands up.
        return string.Join(
            ", ",
            $"{AzureGpSubstrateConfig.PoolVmSizeVar}={substrate.PoolVmSize}",
            $"{AzureGpSubstrateConfig.MaxNodesVar}={substrate.MaxNodes}",
            $"worker_image={(string.IsNullOrWhiteSpace(substrate.Image) ? "(acr-default)" : substrate.Image)}",
            $"acr={(string.IsNullOrWhiteSpace(substrate.AcrName) ? "(module-derived)" : substrate.AcrName)}");
    }

    // Build the plan-first Azure GP substrate step plan WITHOUT any backend call, so the terraform
    // commands shown are exactly what a gated execute would run (plan in plan mode, apply when
    // gated) against the per-env substrate stack.
    private RuntimeAdapterWorkflow BuildAzureGpWorkflow(AzureGpRuntimeAdapter adapter)
    {
        RuntimeAdapterRequest request = new(
            Service: "geoprocessing",
            Environments: runtime.AllowedEnvironments,
            Revision: "HEAD",
            Action: "apply",
            ChangeSummary: "azure gp per-env substrate provision",
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            TerraformLocalPath: runtime.TerraformLocalPath,
            // PLAN-FIRST: DryRun is true unless the agent is in execute mode, so the
            // apply-infra step degrades to terraform plan and mutates nothing by default.
            DryRun: runtime.ExecutionMode != ExecutionMode.Execute,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        return adapter.BuildWorkflow(request);
    }

    private OperationResponse EnrichAzureGpProposal(
        OperationResponse proposal,
        AzureGpRuntimeAdapter adapter,
        IReadOnlyList<string> substrateFindings)
    {
        RuntimeAdapterWorkflow workflow = BuildAzureGpWorkflow(adapter);
        bool planFirst = runtime.ExecutionMode != ExecutionMode.Execute;

        List<string> findings =
        [
            .. proposal.Findings,
            $"Azure GP runtime adapter target: {adapter.Capability.Target} ({adapter.Capability.Family}).",
            $"Azure GP substrate provision mode: {(planFirst ? "plan-first (terraform plan only; provisions nothing)" : "execute (gated terraform apply)")}.",
            .. substrateFindings,
            $"Validate step: {workflow.Validate.Summary}",
            $"Plan-infra command: {workflow.PlanInfrastructure.SuggestedCommands.FirstOrDefault() ?? "(none)"}",
            $"Apply-infra step: {workflow.ApplyInfrastructure.Summary}"
        ];

        List<string> checks =
        [
            .. proposal.ValidationChecks,
            .. workflow.Validate.ValidationChecks,
            .. workflow.PlanInfrastructure.ValidationChecks,
            planFirst ? "azure-gp-apply-mode:plan-only" : "azure-gp-apply-mode:write-enabled-gated"
        ];

        List<string> risks =
        [
            .. proposal.Risks,
            .. workflow.PlanInfrastructure.Risks,
            .. workflow.ApplyInfrastructure.Risks
        ];

        return proposal with
        {
            Summary = $"Azure GP per-env substrate provision proposal ({adapter.Capability.Target}) for `{adapter.Substrate.WorkloadId}` — {(planFirst ? "plan-first, advisory" : "gated execute")}.",
            Findings = findings,
            ValidationChecks = checks,
            Risks = risks
        };
    }
}
