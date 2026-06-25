namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// Azure geoprocessing (GP) runtime adapter: provisions / updates the durable PER-ENVIRONMENT
/// Azure GP substrate — the Azure Batch account, a SINGLE worker pool (VM size + node ceiling),
/// the user-assigned task identity, the ACR holding the GDAL worker image, and the output blob
/// container — in the honua-iac <c>azure-gp</c> module (gated <c>enable_azure_gp_substrate=true</c>).
/// It is the Azure equivalent of the AWS <see cref="GpRuntimeAdapter"/> (#107/#108).
///
/// This runs RARELY: when GP capability is added or updated in an environment, through the
/// existing plan-first / approval-gated path. It is NOT a per-job provision: per-job sizing
/// (task timeout / retry / image / env) is a pure runtime concern the server applies at task-add
/// time — NO terraform, NO agent, per job. UNLIKE AWS, Azure sizes per-POOL and the MVP is a
/// SINGLE pool, so there is NO tier selector here (deferred fast-follow once the iac adds pools).
///
/// The adapter NEVER applies in plan mode: the real <c>terraform apply</c> stays behind the
/// agent's ExecutionMode / ExecutionTier / ApprovalMode gates exactly like the other adapters;
/// in plan mode every "apply" step degrades to a <c>terraform plan</c>. It binds to the substrate
/// OUTPUTS (see <see cref="AzureGpSubstrateOutputs"/>), never to input-variable names.
/// </summary>
internal sealed class AzureGpRuntimeAdapter : RuntimeAdapterBase
{
    internal const string TargetName = "azure-gp";

    private readonly AzureGpSubstrateConfig _substrate;

    internal AzureGpRuntimeAdapter(AzureGpSubstrateConfig? substrate = null)
    {
        _substrate = substrate ?? AzureGpSubstrateConfig.Default;
    }

    /// <summary>
    /// Default substrate instance used for capability resolution in the registry/catalog and as
    /// the conservative per-env baseline (single Standard_D4s_v5 pool, 8 nodes, create-ACR).
    /// </summary>
    internal static AzureGpRuntimeAdapter Default { get; } = new();

    internal AzureGpSubstrateConfig Substrate => _substrate;

    public override RuntimeAdapterCapability Capability =>
        RuntimeAdapterCatalog.BuildGeoprocessingBatchCapability(TargetName);

    protected override string TerraformModule => "azure-gp";

    // Infra IS the deliverable for the Azure GP substrate; there is no separate release backend.
    // Rollback is a terraform-state revert of the substrate stack.
    protected override string ReleaseBackend => "terraform-azure-gp-substrate";

    protected override string RollbackHandle => "azure-gp-substrate-terraform-revert";

    protected override string TargetDisplayName => "azure-gp (per-env Azure Batch single-pool substrate)";

    /// <summary>
    /// Validate the per-env substrate config and confirm the substrate stack is present. The
    /// validation is substrate-shaped (image/ACR/pool VM size/node ceiling), not per-job: there is
    /// no per-job profile and no pool-tier to reject here (single-pool MVP).
    /// </summary>
    public override RuntimeAdapterStepPlan Validate(RuntimeAdapterRequest request)
    {
        List<string> checks =
        [
            $"target-capability:{Capability.Target}",
            $"execution-tier:{request.ExecutionTier.ToConfigValue()}",
            $"azure-gp-substrate-pool:{_substrate.PoolVmSize}",
            $"azure-gp-substrate-max-nodes:{_substrate.MaxNodes}"
        ];

        return new RuntimeAdapterStepPlan(
            Operation: "validate",
            Summary: $"Validate the per-env Azure GP substrate config for `{TargetDisplayName}` against execution tier `{request.ExecutionTier.ToConfigValue()}` ({DescribeSubstrate()}). The substrate is provisioned once per env; per-job sizing is a task-add-time concern, not validated here.",
            SuggestedCommands:
            [
                $"test -d {ShellQuote(System.IO.Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "modules", TerraformModule))}",
                $"{request.GitOpsTool} validate-target --target {ShellQuote(Capability.Target)} --tier {ShellQuote(request.ExecutionTier.ToConfigValue())}"
            ],
            ValidationChecks: checks,
            Risks:
            [
                "Provisioning the Azure GP substrate stands up durable Azure Batch infra (account, single worker pool, task identity, ACR, output container) per env; an over-broad max-nodes ceiling inflates the pool's autoscale headroom.",
                "Per-job task timeout/retry/image are NOT provisioned here: they are per-task overrides the server applies at task-add time.",
                "Azure sizes per-POOL and the MVP is a SINGLE pool — there is no tier selection (the multi-pool tier selector is a deferred fast-follow)."
            ]);
    }

    public override RuntimeAdapterStepPlan PlanInfrastructure(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-infra",
            Summary: $"Plan the per-env Azure GP substrate provision for `{TargetDisplayName}` ({DescribeSubstrate()}). Read-only terraform plan; stands up nothing.",
            SuggestedCommands:
            [
                BuildTerraformCommand(request, "plan")
            ],
            ValidationChecks:
            [
                $"infra-plan:{Capability.Target}",
                $"azure-gp-substrate-vars:{AzureGpSubstrateConfig.EnableVar}=true",
                $"azure-gp-substrate-outputs:{string.Join(",", AzureGpSubstrateOutputs.All)}"
            ],
            Risks:
            [
                "The substrate plan can be stale if the validated honua-iac ref changes before execution.",
                "The substrate provisions a SINGLE worker pool; per-job sizing sets only the per-task overrides (timeout/retry/image) at task-add time — none of that is templated here.",
                "Downstream consumers (the server) bind to the substrate OUTPUTS (gp_batch_account_url / gp_pool_id / gp_task_identity_id / gp_acr_login_server / gp_output_container_url / gp_control_plane_backend_name), not to input-variable names."
            ]);
    }

    public override RuntimeAdapterStepPlan ApplyInfrastructure(RuntimeAdapterRequest request)
    {
        // PLAN-FIRST: in plan mode (DryRun) the "apply" step is a terraform plan and mutates
        // nothing. Only a gated execute pass renders the real apply command.
        bool planOnly = request.DryRun;
        return new RuntimeAdapterStepPlan(
            Operation: "apply-infra",
            Summary: planOnly
                ? $"Plan-only: would provision/update the per-env Azure GP substrate for `{TargetDisplayName}` ({DescribeSubstrate()}). No apply issued in plan mode."
                : $"Apply the per-env Azure GP substrate for `{TargetDisplayName}` ({DescribeSubstrate()}) under the satisfied execution + approval gate.",
            SuggestedCommands:
            [
                BuildTerraformCommand(request, planOnly ? "plan" : "apply")
            ],
            ValidationChecks:
            [
                $"infra-apply-gate:{Capability.Target}",
                planOnly ? "azure-gp-apply-mode:plan-only" : "azure-gp-apply-mode:write-enabled"
            ],
            Risks:
            [
                "Provisioning the substrate before the GP worker image exists in the ACR leaves the pool unrunnable until the image is pushed.",
                "Substrate provisioning is per-env and infrequent; do not invoke it per job (per-job sizing is a task-add-time runtime concern with zero infra change)."
            ]);
    }

    // Release planning/apply are not applicable to the substrate (infra IS the deliverable).
    // Surface them as no-op plan steps that redirect to the infra path.
    public override RuntimeAdapterStepPlan PlanRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-release",
            Summary: $"`{TargetDisplayName}` has no separate release backend: the durable substrate (infra) IS the deliverable. See plan-infra.",
            SuggestedCommands: [],
            ValidationChecks: [$"release-plan:not-applicable:{Capability.Target}"],
            Risks: ["Treating the Azure GP substrate as a per-deploy release misreads its lifecycle: it is provisioned once per env and jobs run against it via the server's task-add path."]);
    }

    public override RuntimeAdapterStepPlan ApplyRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "apply-release",
            Summary: $"`{TargetDisplayName}` has no separate release apply: provisioning runs through apply-infra under the same gates.",
            SuggestedCommands: [],
            ValidationChecks: [$"release-apply:not-applicable:{Capability.Target}"],
            Risks: ["The Azure GP substrate has no traffic-shift or revision-weight surface to apply."]);
    }

    public override RuntimeAdapterStepPlan Verify(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "verify",
            Summary: $"Verify the per-env Azure GP substrate for `{TargetDisplayName}` exposes its OUTPUTS (account url + single pool id + task identity + ACR + output container) so the server can submit tasks against it (workloadId `{_substrate.WorkloadId}`).",
            SuggestedCommands:
            [
                $"terraform -chdir {ShellQuote(TerraformChdir(request))} output -raw {AzureGpSubstrateOutputs.BatchAccountUrl}",
                $"terraform -chdir {ShellQuote(TerraformChdir(request))} output -raw {AzureGpSubstrateOutputs.PoolId}",
                $"{request.GitOpsTool} verify --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)}"
            ],
            ValidationChecks:
            [
                $"adapter-verify:{Capability.Target}",
                $"azure-gp-substrate-output:{AzureGpSubstrateOutputs.BatchAccountUrl}",
                $"azure-gp-substrate-output:{AzureGpSubstrateOutputs.PoolId}",
                $"azure-gp-substrate-output:{AzureGpSubstrateOutputs.OutputContainerUrl}",
                $"azure-gp-control-plane-backend:{AzureGpSubstrateOutputs.ControlPlaneBackendIdentifier}",
                $"azure-gp-workload-registered:{_substrate.WorkloadId}"
            ],
            Risks:
            [
                "A substrate that registers clean but has a missing pool id or output container will fail at task-add time when the server submits against it.",
                "Binding to an input-variable name instead of an output reintroduces the brittle coupling the AWS adapter removed in #108."
            ]);
    }

    public override RuntimeAdapterStepPlan DetectDrift(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "drift",
            Summary: $"Detect drift between the desired per-env Azure GP substrate shape ({DescribeSubstrate()}) and the active Azure Batch substrate for `{TargetDisplayName}`.",
            SuggestedCommands:
            [
                BuildTerraformCommand(request, "plan", driftDetect: true)
            ],
            ValidationChecks:
            [
                $"drift-report:{Capability.Target}"
            ],
            Risks:
            [
                "Silent drift in the Batch account, the worker pool, the task identity, or the ACR means tasks submit against an unintended substrate or a missing pool."
            ]);
    }

    protected override string BuildRollbackCommand(RuntimeAdapterRequest request)
    {
        // Rollback = revert the substrate stack via terraform (re-apply the prior-known-good
        // substrate config, or destroy this substrate). Plan-first by default.
        return request.DryRun
            ? BuildTerraformCommand(request, "plan")
            : $"terraform -chdir {ShellQuote(TerraformChdir(request))} apply {AzureGpSubstrateVarFlags()} # re-apply prior known-good Azure GP substrate config to revert";
    }

    /// <summary>Build a terraform command at the substrate stack dir with the per-env substrate var flags.</summary>
    private string BuildTerraformCommand(RuntimeAdapterRequest request, string verb, bool driftDetect = false)
    {
        string suffix = driftDetect ? " -detailed-exitcode" : string.Empty;
        return $"terraform -chdir {ShellQuote(TerraformChdir(request))} {verb} {AzureGpSubstrateVarFlags()}{suffix}";
    }

    private static string TerraformChdir(RuntimeAdapterRequest request) =>
        System.IO.Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "examples", "azure-cert");

    /// <summary>Render the ordered <c>-var 'name=value'</c> flags from the per-env substrate config.</summary>
    private string AzureGpSubstrateVarFlags()
    {
        IReadOnlyList<AzureGpSubstrateVar> vars = _substrate.ToSubstrateVars();
        return string.Join(" ", vars.Select(v => $"-var {ShellQuote($"{v.Name}={v.Value}")}"));
    }

    private string DescribeSubstrate() =>
        $"pool-vm-size={_substrate.PoolVmSize}, max-nodes={_substrate.MaxNodes}, " +
        $"image={(string.IsNullOrWhiteSpace(_substrate.Image) ? "acr-default" : _substrate.Image!)}, " +
        $"acr={(string.IsNullOrWhiteSpace(_substrate.AcrName) ? "(unset)" : _substrate.AcrName)}, " +
        $"worker-gdal-acr={(_substrate.CreateWorkerGdalAcr ? "create" : "reuse")}";
}
