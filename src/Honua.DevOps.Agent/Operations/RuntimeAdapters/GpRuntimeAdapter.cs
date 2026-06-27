namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// Geoprocessing (GP) runtime adapter: provisions / updates the durable PER-ENVIRONMENT GP
/// substrate — the AWS Batch compute-env (Fargate-Spot), job queue, IAM roles, ECR repo, and a
/// POOL of job-definition ephemeral-storage tiers (s/m/l/xl) — in the honua-iac GP substrate
/// stack (gated <c>enable_gp_batch=true</c>).
///
/// This runs RARELY: when GP capability is added or updated in an environment ("provision GP
/// capability in env X"), through the existing plan-first / approval-gated path. It is NOT a
/// per-job provision: per-job sizing (vCPU/memory/timeout/retry + tier selection) is a pure
/// runtime concern the server applies at AWS Batch <c>SubmitJob</c> time with ContainerOverrides
/// + resourceRequirements + a job-definition tier pick — NO terraform, NO agent, per job.
///
/// The adapter NEVER applies in plan mode: the real <c>terraform apply</c> stays behind the
/// agent's ExecutionMode / ExecutionTier / ApprovalMode gates exactly like the other adapters;
/// in plan mode every "apply" step degrades to a <c>terraform plan</c>. It binds to the
/// substrate OUTPUTS / ARNs (see <see cref="GpSubstrateOutputs"/>), never to input-variable names.
/// </summary>
internal sealed class GpRuntimeAdapter : RuntimeAdapterBase
{
    internal const string TargetName = "gp";

    private readonly GpSubstrateConfig _substrate;

    internal GpRuntimeAdapter(GpSubstrateConfig? substrate = null)
    {
        _substrate = substrate ?? GpSubstrateConfig.Default;
    }

    /// <summary>
    /// Default substrate instance used for capability resolution in the registry/catalog and as
    /// the conservative per-env baseline (x86_64 Fargate-Spot, full s/m/l/xl tier pool).
    /// </summary>
    internal static GpRuntimeAdapter Default { get; } = new();

    internal GpSubstrateConfig Substrate => _substrate;

    public override RuntimeAdapterCapability Capability =>
        RuntimeAdapterCatalog.BuildGeoprocessingBatchCapability(TargetName);

    protected override string TerraformModule => "aws-serverless";

    // Infra IS the deliverable for the GP substrate; there is no separate release backend.
    // Rollback is a terraform-state revert of the substrate stack.
    protected override string ReleaseBackend => "terraform-gp-substrate";

    protected override string RollbackHandle => "gp-substrate-terraform-revert";

    protected override string TargetDisplayName => "gp (per-env AWS Batch substrate)";

    /// <summary>
    /// Validate the per-env substrate config and confirm the substrate stack is present. The
    /// validation is substrate-shaped (image/arch/tier-pool/compute ceiling), not per-job: there
    /// is no per-job profile to reject here. A GPU need is NOT validated here either — GPU is a
    /// runtime sizing-hint note (the default Fargate substrate has no GPU tiers), not a substrate
    /// provisioning input.
    /// </summary>
    public override RuntimeAdapterStepPlan Validate(RuntimeAdapterRequest request)
    {
        List<string> checks =
        [
            $"target-capability:{Capability.Target}",
            $"execution-tier:{request.ExecutionTier.ToConfigValue()}",
            $"gp-substrate-arch:{GpSubstrateConfig.ToTerraformArchitecture(_substrate.Architecture)}",
            $"gp-substrate-tiers:{TierPoolToken()}"
        ];

        return new RuntimeAdapterStepPlan(
            Operation: "validate",
            Summary: $"Validate the per-env GP substrate config for `{TargetDisplayName}` against execution tier `{request.ExecutionTier.ToConfigValue()}` ({DescribeSubstrate()}). The substrate is provisioned once per env; per-job sizing is a SubmitJob-time concern, not validated here.",
            SuggestedCommands:
            [
                $"test -d {ShellQuote(System.IO.Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "modules", TerraformModule))}",
                $"{request.GitOpsTool} validate-target --target {ShellQuote(Capability.Target)} --tier {ShellQuote(request.ExecutionTier.ToConfigValue())}"
            ],
            ValidationChecks: checks,
            Risks:
            [
                "Provisioning the GP substrate stands up durable AWS Batch infra (compute-env, queue, IAM, ECR, tier pool) per env; an over-broad max-vcpus ceiling can inflate the compute-env scaling headroom.",
                "Per-job vCPU/memory/timeout/retry are NOT provisioned here: they are SubmitJob overrides the server applies at runtime against the pooled tiers."
            ]);
    }

    public override RuntimeAdapterStepPlan PlanInfrastructure(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-infra",
            Summary: $"Plan the per-env GP substrate provision for `{TargetDisplayName}` ({DescribeSubstrate()}). Read-only terraform plan; stands up nothing.",
            SuggestedCommands:
            [
                BuildTerraformCommand(request, "plan")
            ],
            ValidationChecks:
            [
                $"infra-plan:{Capability.Target}",
                $"gp-substrate-vars:{GpSubstrateConfig.EnableVar}=true",
                $"gp-substrate-outputs:{string.Join(",", GpSubstrateOutputs.All)}"
            ],
            Risks:
            [
                "The substrate plan can be stale if the validated honua-iac ref changes before execution.",
                "The substrate provisions a POOL of job-definition tiers (s/m/l/xl); per-job sizing selects a tier and overrides vCPU/memory/timeout/retry at SubmitJob time — none of that is templated here.",
                "Downstream consumers (the server) bind to the substrate OUTPUTS (gp_job_queue_arn / gp_job_definition_arns / gp_compute_environment_arn / gp_job_role_arn / gp_execution_role_arn / gp_worker_gdal_repository_url), not to input-variable names."
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
                ? $"Plan-only: would provision/update the per-env GP substrate for `{TargetDisplayName}` ({DescribeSubstrate()}). No apply issued in plan mode."
                : $"Apply the per-env GP substrate for `{TargetDisplayName}` ({DescribeSubstrate()}) under the satisfied execution + approval gate.",
            SuggestedCommands:
            [
                BuildTerraformCommand(request, planOnly ? "plan" : "apply")
            ],
            ValidationChecks:
            [
                $"infra-apply-gate:{Capability.Target}",
                planOnly ? "gp-apply-mode:plan-only" : "gp-apply-mode:write-enabled"
            ],
            Risks:
            [
                "Provisioning the substrate before the GP worker image exists in the ECR repo leaves the tier pool unrunnable until the image is pushed.",
                "Substrate provisioning is per-env and infrequent; do not invoke it per job (per-job sizing is a SubmitJob-time runtime concern with zero infra change)."
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
            Risks: ["Treating the GP substrate as a per-deploy release misreads its lifecycle: it is provisioned once per env and jobs run against it via the server's SubmitJob path."]);
    }

    public override RuntimeAdapterStepPlan ApplyRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "apply-release",
            Summary: $"`{TargetDisplayName}` has no separate release apply: provisioning runs through apply-infra under the same gates.",
            SuggestedCommands: [],
            ValidationChecks: [$"release-apply:not-applicable:{Capability.Target}"],
            Risks: ["The GP substrate has no traffic-shift or revision-weight surface to apply."]);
    }

    public override RuntimeAdapterStepPlan Verify(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "verify",
            Summary: $"Verify the per-env GP substrate for `{TargetDisplayName}` exposes its OUTPUTS (queue + tier-keyed job-definition pool ARNs) so the server can submit jobs against it (workloadId `{_substrate.WorkloadId}`).",
            SuggestedCommands:
            [
                $"terraform -chdir {ShellQuote(TerraformChdir(request))} output -raw {GpSubstrateOutputs.JobQueueArn}",
                $"terraform -chdir {ShellQuote(TerraformChdir(request))} output -json {GpSubstrateOutputs.JobDefinitionArns}",
                $"{request.GitOpsTool} verify --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)}"
            ],
            ValidationChecks:
            [
                $"adapter-verify:{Capability.Target}",
                $"gp-substrate-output:{GpSubstrateOutputs.JobQueueArn}",
                $"gp-substrate-output:{GpSubstrateOutputs.JobDefinitionArns}",
                $"gp-substrate-output:{GpSubstrateOutputs.ComputeEnvironmentArn}",
                $"gp-workload-registered:{_substrate.WorkloadId}"
            ],
            Risks:
            [
                "A substrate that registers clean but has a missing/empty tier in gp_job_definition_arns will fail at SubmitJob time when the server selects that tier.",
                "Binding to an input-variable name instead of an output ARN reintroduces the brittle coupling this design removed."
            ]);
    }

    public override RuntimeAdapterStepPlan DetectDrift(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "drift",
            Summary: $"Detect drift between the desired per-env GP substrate shape ({DescribeSubstrate()}) and the active AWS Batch substrate for `{TargetDisplayName}`.",
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
                "Silent drift in the compute-env, queue, IAM, or tier pool means jobs submit against an unintended substrate or a missing tier."
            ]);
    }

    protected override string BuildRollbackCommand(RuntimeAdapterRequest request)
    {
        // Rollback = revert the substrate stack via terraform (re-apply the prior-known-good
        // substrate config, or destroy this substrate). Plan-first by default.
        return request.DryRun
            ? BuildTerraformCommand(request, "plan")
            : $"terraform -chdir {ShellQuote(TerraformChdir(request))} apply {GpSubstrateVarFlags()} # re-apply prior known-good GP substrate config to revert";
    }

    /// <summary>Build a terraform command at the substrate stack dir with the per-env substrate var flags.</summary>
    private string BuildTerraformCommand(RuntimeAdapterRequest request, string verb, bool driftDetect = false)
    {
        string suffix = driftDetect ? " -detailed-exitcode" : string.Empty;
        return $"terraform -chdir {ShellQuote(TerraformChdir(request))} {verb} {GpSubstrateVarFlags()}{suffix}";
    }

    private static string TerraformChdir(RuntimeAdapterRequest request) =>
        System.IO.Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "examples", "aws-cert");

    /// <summary>Render the ordered <c>-var 'name=value'</c> flags from the per-env substrate config.</summary>
    private string GpSubstrateVarFlags()
    {
        IReadOnlyList<GpSubstrateVar> vars = _substrate.ToSubstrateVars();
        return string.Join(" ", vars.Select(v => $"-var {ShellQuote($"{v.Name}={v.Value}")}"));
    }

    private string DescribeSubstrate() =>
        $"arch={GpSubstrateConfig.ToTerraformArchitecture(_substrate.Architecture)}, " +
        $"max-vcpus={_substrate.MaxVcpus}, tiers={TierPoolToken()}, " +
        $"image={(string.IsNullOrWhiteSpace(_substrate.Image) ? "lambda-default" : _substrate.Image!)}, " +
        $"worker-gdal-repo={(_substrate.CreateWorkerGdalRepo ? "create" : "reuse")}";

    private string TierPoolToken() =>
        string.Join("/", _substrate.EffectiveTiers.Select(GpResourceProfile.TierToken));
}
