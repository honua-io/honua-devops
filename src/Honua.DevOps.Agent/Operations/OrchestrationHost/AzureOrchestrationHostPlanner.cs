using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.OrchestrationHost;

internal static class AzureOrchestrationHostPlanner
{
    private const string HostTarget = "microsoft-agent-framework-azure";

    internal static OrchestrationHostPlan Build(
        OperatorWorkflowFamily workflowFamily,
        string environment,
        string operatorGoal,
        string? packageReference,
        string? deploymentTarget,
        bool publishExternally,
        OperationRuntime runtime,
        OperatorPolicyModel policy)
    {
        List<OrchestrationHostStagePlan> stages =
        [
            Stage(
                OrchestrationStageKind.CaptureIntent,
                "planned",
                "Interpret the operator goal and choose the workflow family without filling unknown required inputs.",
                "Validate intent shape and preserve missing fields for clarification.",
                "geospatial-mcp: elicitation and planning semantics",
                "Host the agent session and persist the initial workflow envelope.",
                ["intent-schema", "workflow-family"]),
            Stage(
                OrchestrationStageKind.GroundCandidates,
                "planned",
                "Rank candidate datasets, services, templates, packages, and deployment targets.",
                "Fetch catalog, permissions, package metadata, and deployment state from canonical surfaces.",
                "geospatial-mcp: resources and tool taxonomy",
                "Bind Azure trace context to MCP resource/tool calls and keep grounding evidence.",
                ["mcp-resource-grounding", "catalog-permission-check"]),
            Stage(
                OrchestrationStageKind.Clarify,
                RequiresClarification(workflowFamily, publishExternally) ? "required-before-execution" : "conditional",
                "Draft focused follow-up questions when required inputs or high-risk choices are missing.",
                "Enforce clarification policy before execution, publication, sharing, or approval boundaries.",
                "geospatial-mcp: elicitation",
                "Surface required questions through the Agent Framework turn instead of guessing.",
                ["clarification-policy", "assumption-record"]),
            Stage(
                OrchestrationStageKind.CompilePlan,
                "planned",
                "Propose the workflow graph and stage ordering.",
                "Normalize the plan into the deterministic stage model.",
                ContractForPlan(workflowFamily),
                "Attach policy, environment, and trace metadata to the compiled plan.",
                [$"{workflowFamily.ToConfigValue()}-workflow-family", "deterministic-stage-model"]),
            Stage(
                OrchestrationStageKind.ValidatePlan,
                "planned",
                "Explain plan risks and alternatives.",
                "Validate schema, capability support, authorization, package compatibility, and policy gates.",
                "honua-server: authorization, package, deployment, and result contracts",
                "Evaluate host policy before any gRPC execution or publication request.",
                ["schema-validation", "capability-validation", "authorization-policy"]),
            Stage(
                OrchestrationStageKind.DryRun,
                "planned",
                "Summarize estimates and side effects for operator review.",
                "Call dry-run or estimate semantics on the typed execution plane.",
                "geospatial-grpc: dry-run and estimation semantics",
                "Record estimated duration, artifacts, side effects, and approval requirement.",
                ["grpc-dry-run-or-estimate", "side-effect-summary"])
        ];

        stages.Add(Stage(
            OrchestrationStageKind.Execute,
            runtime.ExecutionMode == ExecutionMode.Execute ? "policy-gated" : "dry-run-only",
            "No model-side state transition authority; the model can only summarize execution progress.",
            "Run the accepted plan through gRPC jobs or honua-server deployment transitions.",
            ContractForExecution(workflowFamily),
            "Correlate Agent Framework session, job id, Azure Monitor trace, and evidence bundle.",
            [
                runtime.ExecutionMode == ExecutionMode.Execute ? "operator-policy-gate" : "dry-run-enforced",
                "grpc-job-progress",
                "provenance-record"
            ]));

        if (workflowFamily is OperatorWorkflowFamily.Analyze or OperatorWorkflowFamily.Publish or OperatorWorkflowFamily.Build)
        {
            stages.Add(Stage(
                OrchestrationStageKind.ComposeMap,
                "planned",
                "Suggest map style, labels, popups, layout, and package usefulness criteria.",
                "Bind artifacts to MapPackage, render preview, and validate style/template/source bindings.",
                "geospatial-grpc RenderService and honua-server MapPackage contract",
                "Capture preview artifacts and package references for downstream eval.",
                ["map-package-contract", "style-binding", "preview-artifact"]));
        }

        if (workflowFamily == OperatorWorkflowFamily.Build)
        {
            stages.Add(Stage(
                OrchestrationStageKind.ComposeApp,
                "planned",
                "Suggest app structure and operator-facing UX intent.",
                "Generate or bind AppPackage against the JS-first runtime contract.",
                "geospatial-grpc BuilderService and honua-sdk-js runtime package",
                "Record app package identity, preview route, and package compatibility.",
                ["app-package-contract", "sdk-js-runtime-target", "preview-artifact"]));
        }

        if (publishExternally || workflowFamily is OperatorWorkflowFamily.Publish or OperatorWorkflowFamily.Deploy)
        {
            stages.Add(Stage(
                OrchestrationStageKind.Publish,
                publishExternally ? "approval-required" : "planned",
                "Summarize publication or deployment intent and operator-visible tradeoffs.",
                "Apply publication/deployment state transitions through canonical server contracts.",
                "honua-server deployment lifecycle and geospatial-grpc DeploymentService",
                "Attach approval record, deployment route, URL, revision, runtime config, and publication state.",
                PublishChecks(publishExternally)));
        }

        stages.Add(Stage(
            OrchestrationStageKind.ReturnResultPackage,
            "planned",
            "Explain final result, assumptions, residual risks, and follow-up options.",
            "Emit deterministic workflow envelope with stage results, package references, and provenance.",
            "honua-server deterministic result package",
            "Persist eval-ready result metadata for Claude, Codex, and local portability lanes.",
            ["deterministic-result-envelope", "eval-report-ready", "trace-correlation"]));

        return new OrchestrationHostPlan(
            WorkflowFamily: workflowFamily,
            HostTarget: HostTarget,
            Environment: environment,
            OperatorGoal: operatorGoal,
            PackageReference: string.IsNullOrWhiteSpace(packageReference) ? null : packageReference.Trim(),
            DeploymentTarget: string.IsNullOrWhiteSpace(deploymentTarget) ? null : deploymentTarget.Trim(),
            PublishExternally: publishExternally,
            ContractSurfaces: BuildContractSurfaces(workflowFamily),
            AzureIntegrationPoints: BuildAzureIntegrationPoints(runtime, policy),
            Stages: stages,
            EvaluationHooks: BuildEvaluationHooks(),
            BoundaryRules: BuildBoundaryRules());
    }

    private static OrchestrationHostStagePlan Stage(
        OrchestrationStageKind stage,
        string status,
        string modelRole,
        string deterministicRole,
        string contractSurface,
        string azureHostResponsibility,
        IReadOnlyList<string> requiredChecks)
    {
        return new OrchestrationHostStagePlan(
            stage,
            status,
            modelRole,
            deterministicRole,
            contractSurface,
            azureHostResponsibility,
            requiredChecks);
    }

    private static bool RequiresClarification(OperatorWorkflowFamily workflowFamily, bool publishExternally)
    {
        return publishExternally || workflowFamily is OperatorWorkflowFamily.Publish or OperatorWorkflowFamily.Deploy;
    }

    private static string ContractForPlan(OperatorWorkflowFamily workflowFamily)
    {
        return workflowFamily switch
        {
            OperatorWorkflowFamily.Analyze => "geospatial-mcp: analysis planning",
            OperatorWorkflowFamily.Publish => "geospatial-mcp: publishing planning",
            OperatorWorkflowFamily.Build => "geospatial-mcp: builder planning",
            OperatorWorkflowFamily.Deploy => "geospatial-mcp: automate/deploy planning",
            _ => throw new InvalidOperationException("Unsupported operator workflow family.")
        };
    }

    private static string ContractForExecution(OperatorWorkflowFamily workflowFamily)
    {
        return workflowFamily switch
        {
            OperatorWorkflowFamily.Analyze => "geospatial-grpc ProcessService",
            OperatorWorkflowFamily.Publish => "geospatial-grpc PipelineService",
            OperatorWorkflowFamily.Build => "geospatial-grpc RenderService and BuilderService",
            OperatorWorkflowFamily.Deploy => "geospatial-grpc DeploymentService and honua-server deployment lifecycle",
            _ => throw new InvalidOperationException("Unsupported operator workflow family.")
        };
    }

    private static IReadOnlyList<string> PublishChecks(bool publishExternally)
    {
        List<string> checks =
        [
            "deployment-state-contract",
            "route-url-revision-runtime-config",
            "publication-state"
        ];

        if (publishExternally)
        {
            checks.Add("approval-record");
        }

        return checks;
    }

    private static IReadOnlyList<string> BuildContractSurfaces(OperatorWorkflowFamily workflowFamily)
    {
        List<string> surfaces =
        [
            "geospatial-mcp: interaction plane for tools, resources, prompts, planning, and elicitation.",
            "geospatial-grpc: typed execution plane for dry-run, jobs, progress, results, artifacts, and errors.",
            "honua-server: deterministic validation, package lifecycle, deployment state, and publication surfaces.",
            "honua-devops: Microsoft Agent Framework host, policy envelope, evidence capture, and Azure tracing."
        ];

        surfaces.Add(workflowFamily switch
        {
            OperatorWorkflowFamily.Analyze => "Workflow-specific services: ProcessService plus RenderService for map packages.",
            OperatorWorkflowFamily.Publish => "Workflow-specific services: PipelineService plus publish/deployment lifecycle.",
            OperatorWorkflowFamily.Build => "Workflow-specific services: RenderService, BuilderService, and honua-sdk-js package runtime.",
            OperatorWorkflowFamily.Deploy => "Workflow-specific services: DeploymentService and honua-server hosted deployment state.",
            _ => throw new InvalidOperationException("Unsupported operator workflow family.")
        });

        return surfaces;
    }

    private static IReadOnlyList<string> BuildAzureIntegrationPoints(OperationRuntime runtime, OperatorPolicyModel policy)
    {
        return
        [
            "Microsoft Agent Framework hosts the agent session and tool-call loop.",
            "Azure Monitor or OpenTelemetry carries trace correlation across agent turns, MCP calls, gRPC jobs, and deployment state.",
            "Managed identity or Key Vault supplies provider and backend secrets; the host should not persist raw credentials.",
            $"Operator policy uses approval mode `{policy.ApprovalMode.ToConfigValue()}` and execution tier `{runtime.ExecutionTier.ToConfigValue()}`.",
            $"Runtime adapters remain available for deployment targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}."
        ];
    }

    private static IReadOnlyList<string> BuildEvaluationHooks()
    {
        return
        [
            "Record deterministic stage status for clarification quality, plan validity, execution success, and result correctness.",
            "Capture package usefulness for MapPackage/AppPackage outputs and publication/deployment usefulness for hosted surfaces.",
            "Keep the same stage envelope consumable by Claude, Codex, and local portability lanes.",
            "Retain enough trace metadata to compare model proposal quality against deterministic validation outcomes."
        ];
    }

    private static IReadOnlyList<string> BuildBoundaryRules()
    {
        return
        [
            "Do not redefine MCP tool, resource, prompt, planning, or elicitation semantics.",
            "Do not redefine gRPC service contracts, job models, artifact models, or error envelopes.",
            "Do not redefine honua-server package, route, URL, revision, runtime-config, or publication-state behavior.",
            "Do not allow AI-driven source-data editing; the host can plan and execute deterministic non-editing workflows only.",
            "Keep approval enforcement and evidence capture in the host policy envelope before write-capable actions."
        ];
    }
}
