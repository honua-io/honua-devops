using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
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
                UpdatedAt: timestamp);
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
        bool createdOperation = created.CallResult.IsSuccess && !string.IsNullOrWhiteSpace(operationId);
        string status = createdOperation ? BridgeStatus.Proposed : BridgeStatus.ContractUnavailable;

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
            UpdatedAt: timestamp);

        return BuildProposalResponse(
            proposal,
            steps,
            blockingReason: createdOperation
                ? null
                : "honua-server deploy-control did not return a durable operation id; proposal is blocked, no operation invented.");
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
        string desiredRevision = (root is null ? null : TryGetString(root.Value, "desiredRevision", "desired_revision", "revision")) ?? "unknown";
        string? currentRevision = root is null ? null : TryGetString(root.Value, "currentRevision", "current_revision");
        string serviceName = (root is null ? null : TryGetParameter(root.Value, "service")) ?? "unknown";
        string action = (root is null ? null : TryGetParameter(root.Value, "action")) ?? "unknown";
        string owner = (root is null ? null : TryGetParameter(root.Value, "owner")) ?? "unknown";
        string[] environments = SplitCsv(root is null ? null : TryGetParameter(root.Value, "environments"));
        string timestamp = Timestamp();

        List<EvidenceRef> evidence =
        [
            EvidenceFromCall("deploy-operation", result.CallResult),
            ServerOperationEvidence(normalizedOperationId)
        ];
        List<WorkflowLink> links =
        [
            SelfLink(normalizedOperationId),
            ServerOperationLink(normalizedOperationId),
            GovernedLink("submit", "Submit proposal for execution", normalizedOperationId, "submit"),
            GovernedLink("rollback", "Roll back operation", normalizedOperationId, "rollback")
        ];
        List<SuggestedAction> suggestedActions =
        [
            SubmitSuggestion(normalizedOperationId),
            RollbackSuggestion(normalizedOperationId),
            ReviewEvidenceSuggestion(normalizedOperationId)
        ];

        bool targetsProd = environments.Contains("prod", StringComparer.OrdinalIgnoreCase);
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
            ApprovalRequired: EffectivePolicy.ApprovalMode != ApprovalMode.DirectAllowed || targetsProd,
            WorkflowLinks: links,
            Evidence: evidence,
            SuggestedActions: suggestedActions,
            CreatedAt: timestamp,
            UpdatedAt: timestamp);

        return BuildProposalResponse(
            proposal,
            [ToStep("deploy-operation-status", result.CallResult, mutatesState: false)],
            blockingReason: found ? null : $"No durable deploy-control operation found for `{normalizedOperationId}`.");
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
    // shallow descent into a common envelope ("operation"/"data"/"result") because the
    // exact deploy-control response shape is owned by the server child ticket.
    private static string? TryGetString(JsonElement element, params string[] names)
    {
        string? direct = ReadString(element, names);
        if (direct is not null)
        {
            return direct;
        }

        foreach (string envelope in EnvelopeKeys)
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

        return ReadString(root, [name]);
    }

    private static readonly string[] EnvelopeKeys = ["operation", "data", "result"];
}
