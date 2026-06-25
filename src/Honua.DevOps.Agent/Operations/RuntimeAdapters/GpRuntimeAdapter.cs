namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// Geoprocessing (GP) runtime adapter: maps a per-job <see cref="GpResourceProfile"/>
/// to a UNIQUE per-job AWS Batch provision in the honua-iac <c>modules/aws-serverless</c>
/// module (gated <c>enable_gp_batch=true</c>, instantiated by <c>examples/aws-cert</c>).
///
/// This is the keystone of the "turtles all the way down" model: an AI-designed GP job
/// carries a resource profile, and this adapter sizes + emits a plan-first terraform
/// provision step plan that mints a job definition for that job. It NEVER applies — the
/// real <c>terraform apply</c> stays behind the agent's ExecutionMode / ExecutionTier /
/// ApprovalMode gates exactly like the other adapters; in plan mode every "apply" step
/// degrades to a <c>terraform plan</c>.
///
/// The adapter is profile-bearing (one instance per GP job), unlike the stateless
/// baseline adapters. A default-profile instance (<see cref="Default"/>) is registered in
/// the catalog for capability resolution; the toolkit constructs a profile-specific
/// instance per provision request.
/// </summary>
internal sealed class GpRuntimeAdapter : RuntimeAdapterBase
{
    internal const string TargetName = "gp";

    private readonly GpResourceProfile _profile;
    private readonly GpResourceProfile.ComputePlatform _platform;

    internal GpRuntimeAdapter(
        GpResourceProfile profile,
        GpResourceProfile.ComputePlatform platform = GpResourceProfile.ComputePlatform.FargateSpot)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _platform = platform;
    }

    /// <summary>
    /// Default Fargate-Spot baseline instance used for capability resolution in the
    /// registry/catalog. Provisioning always goes through a profile-specific instance.
    /// </summary>
    internal static GpRuntimeAdapter Default { get; } = new(GpResourceProfile.FargateBaseline);

    internal GpResourceProfile Profile => _profile;

    internal GpResourceProfile.ComputePlatform Platform => _platform;

    public override RuntimeAdapterCapability Capability =>
        RuntimeAdapterCatalog.BuildGeoprocessingBatchCapability(TargetName);

    protected override string TerraformModule => "aws-serverless";

    // Infra IS the deliverable for a per-job Batch provision; there is no separate
    // release backend. Rollback is a terraform-state revert of the job definition.
    protected override string ReleaseBackend => "terraform-batch-job-definition";

    protected override string RollbackHandle => "batch-job-definition-terraform-revert";

    protected override string TargetDisplayName => "gp (per-job AWS Batch)";

    /// <summary>
    /// Validate the GP profile against AWS Batch / honua-iac constraints AND the
    /// requested compute platform (Fargate rejects GPU). On any violation the step plan
    /// surfaces the validation errors as risks and a blocking validation check, so a
    /// GPU-on-Fargate profile is caught here BEFORE any plan/apply step is suggested.
    /// </summary>
    public override RuntimeAdapterStepPlan Validate(RuntimeAdapterRequest request)
    {
        GpProfileValidation validation = _profile.Validate(_platform);

        List<string> checks =
        [
            $"target-capability:{Capability.Target}",
            $"execution-tier:{request.ExecutionTier.ToConfigValue()}",
            $"gp-compute-platform:{PlatformToken()}",
            validation.IsValid ? "gp-profile-valid:true" : "gp-profile-valid:false"
        ];

        List<string> risks =
        [
            $"Provisioning `{Capability.Target}` mints a per-job Batch job definition sized to the GP profile; an oversized profile inflates Fargate task cost."
        ];
        if (!validation.IsValid)
        {
            risks.AddRange(validation.Errors.Select(error => $"GP profile rejected: {error}"));
        }

        return new RuntimeAdapterStepPlan(
            Operation: "validate",
            Summary: validation.IsValid
                ? $"Validate GP resource profile for `{TargetDisplayName}` on platform `{PlatformToken()}` and map it to honua-iac `gp_batch_*` variables ({DescribeProfile()})."
                : $"GP resource profile for `{TargetDisplayName}` is INVALID for platform `{PlatformToken()}`: {string.Join("; ", validation.Errors)}.",
            SuggestedCommands:
            [
                $"test -d {ShellQuote(Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "modules", TerraformModule))}",
                $"{request.GitOpsTool} validate-target --target {ShellQuote(Capability.Target)} --tier {ShellQuote(request.ExecutionTier.ToConfigValue())}"
            ],
            ValidationChecks: checks,
            Risks: risks);
    }

    public override RuntimeAdapterStepPlan PlanInfrastructure(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-infra",
            Summary: $"Plan the per-job AWS Batch provision for `{TargetDisplayName}` ({DescribeProfile()}). Read-only terraform plan; mints nothing.",
            SuggestedCommands:
            [
                BuildTerraformCommand(request, "plan")
            ],
            ValidationChecks:
            [
                $"infra-plan:{Capability.Target}",
                $"gp-batch-vars:{GpBatchTerraformMapping.EnableVar}=true"
            ],
            Risks:
            [
                "A GP job-definition plan can be stale if the validated honua-iac ref changes before execution.",
                "vCPU/memory/timeout/retry/GPU are job-def DEFAULTS overridden per SubmitJob; only image/arch/ephemeral-storage (and GPU on EC2) are uniquely templated here.",
                "The examples/aws-cert root forwards only gp_batch_image/gp_batch_cpu_architecture/create_worker_gdal_repo today; the per-job root must forward the full gp_batch_* set (or set TF_VAR_* / a tfvars file) for the remaining knobs to reach the module."
            ]);
    }

    public override RuntimeAdapterStepPlan ApplyInfrastructure(RuntimeAdapterRequest request)
    {
        // PLAN-FIRST: in plan mode (DryRun) the "apply" step is a terraform plan and
        // mutates nothing. Only a gated execute pass renders the real apply command.
        bool planOnly = request.DryRun;
        return new RuntimeAdapterStepPlan(
            Operation: "apply-infra",
            Summary: planOnly
                ? $"Plan-only: would mint a per-job Batch job definition for `{TargetDisplayName}` ({DescribeProfile()}). No apply issued in plan mode."
                : $"Apply the per-job Batch job definition for `{TargetDisplayName}` ({DescribeProfile()}) under the satisfied execution + approval gate.",
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
                "Applying a per-job job definition before the GP worker image exists in the ECR repo leaves an unrunnable job.",
                "GPU resource requirements require an EC2 Batch compute environment; applying a GPU profile on the Fargate-Spot path fails."
            ]);
    }

    // Release planning/apply are not applicable to a per-job Batch provision (infra IS
    // the deliverable). Surface them as no-op plan steps that redirect to the infra path.
    public override RuntimeAdapterStepPlan PlanRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-release",
            Summary: $"`{TargetDisplayName}` has no separate release backend: the job definition (infra) IS the deliverable. See plan-infra.",
            SuggestedCommands: [],
            ValidationChecks: [$"release-plan:not-applicable:{Capability.Target}"],
            Risks: ["Treating a per-job Batch provision as a long-lived release misreads its lifecycle (the job runs to completion and the compute-env scales to zero)."]);
    }

    public override RuntimeAdapterStepPlan ApplyRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "apply-release",
            Summary: $"`{TargetDisplayName}` has no separate release apply: provisioning runs through apply-infra under the same gates.",
            SuggestedCommands: [],
            ValidationChecks: [$"release-apply:not-applicable:{Capability.Target}"],
            Risks: ["A per-job Batch job definition has no traffic-shift or revision-weight surface to apply."]);
    }

    public override RuntimeAdapterStepPlan Verify(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "verify",
            Summary: $"Verify the per-job Batch job definition for `{TargetDisplayName}` registered and is selectable by the server ControlPlane execution-workload catalog (workloadId `{_profile.WorkloadId}`).",
            SuggestedCommands:
            [
                $"aws batch describe-job-definitions --status ACTIVE --query 'jobDefinitions[?contains(jobDefinitionName, `{_profile.WorkloadId}`)].revision'",
                $"{request.GitOpsTool} verify --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)}"
            ],
            ValidationChecks:
            [
                $"adapter-verify:{Capability.Target}",
                $"gp-workload-registered:{_profile.WorkloadId}"
            ],
            Risks:
            [
                "A registered job definition with a missing/incorrect image will register clean but fail at job runtime."
            ]);
    }

    public override RuntimeAdapterStepPlan DetectDrift(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "drift",
            Summary: $"Detect drift between the templated GP job-definition shape ({DescribeProfile()}) and the active AWS Batch job definition for `{TargetDisplayName}`.",
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
                "Silent drift in image/arch/ephemeral-storage means jobs run against an unintended sizing or binary."
            ]);
    }

    protected override string BuildRollbackCommand(RuntimeAdapterRequest request)
    {
        // Rollback = revert the templated job definition via terraform (re-apply the
        // prior-known-good profile, or destroy this provision). Plan-first by default.
        return request.DryRun
            ? BuildTerraformCommand(request, "plan")
            : $"terraform -chdir {ShellQuote(TerraformChdir(request))} apply {GpVarFlags()} # re-apply prior known-good gp_batch_* profile to revert";
    }

    /// <summary>Build a terraform command at the module-instantiating example dir with the GP profile var flags.</summary>
    private string BuildTerraformCommand(RuntimeAdapterRequest request, string verb, bool driftDetect = false)
    {
        string suffix = driftDetect ? " -detailed-exitcode" : string.Empty;
        return $"terraform -chdir {ShellQuote(TerraformChdir(request))} {verb} {GpVarFlags()}{suffix}";
    }

    private static string TerraformChdir(RuntimeAdapterRequest request) =>
        Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "examples", "aws-cert");

    /// <summary>Render the ordered <c>-var 'name=value'</c> flags from the profile mapping.</summary>
    private string GpVarFlags()
    {
        IReadOnlyList<GpBatchTerraformVar> vars = GpBatchTerraformMapping.ToTerraformVars(_profile);
        return string.Join(" ", vars.Select(v => $"-var {ShellQuote($"{v.Name}={v.Value}")}"));
    }

    private string DescribeProfile() =>
        $"{_profile.Vcpus}vcpu/{_profile.MemoryMib}MiB, arch={GpBatchTerraformMapping.ToTerraformArchitecture(_profile.Architecture)}, " +
        $"gpu={_profile.GpuCount}, ephemeral={(_profile.EphemeralStorageGib?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default")}, " +
        $"timeout={_profile.TimeoutSeconds}s, image={(string.IsNullOrWhiteSpace(_profile.Image) ? "lambda-default" : _profile.Image!)}";

    private string PlatformToken() => _platform == GpResourceProfile.ComputePlatform.Ec2 ? "ec2" : "fargate-spot";
}
