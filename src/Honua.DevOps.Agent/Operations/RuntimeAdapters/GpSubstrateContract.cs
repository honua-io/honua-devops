using System.Globalization;

namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// Per-ENVIRONMENT geoprocessing (GP) substrate configuration: the durable, infrequently
/// changed inputs that stand up / update the GP capability in an environment. This is the
/// input the <see cref="GpRuntimeAdapter"/> provisions through terraform (GitOps-gated), NOT
/// a per-job profile. It describes the substrate as a whole — the container image + CPU
/// architecture the pooled job-definition tiers share, the compute-env ceiling, and which
/// ephemeral-storage tiers to pre-register — never a single job's vCPU/memory/timeout.
///
/// Per-job sizing is entirely separate (see <see cref="GpResourceProfile"/> /
/// <see cref="GpSizingHint"/>): the server selects a tier and applies vCPU/memory/timeout/retry
/// as <c>SubmitJob</c> overrides at runtime, with zero infra change.
/// </summary>
internal sealed record GpSubstrateConfig(
    string? Image = null,
    GpCpuArchitecture Architecture = GpCpuArchitecture.X86_64,
    int MaxVcpus = 256,
    bool CreateWorkerGdalRepo = true,
    string WorkloadId = "geoprocessing-batch",
    IReadOnlyList<GpJobDefinitionTier>? Tiers = null)
{
    /// <summary>The default pooled tier set: all four ephemeral-storage tiers (s/m/l/xl).</summary>
    internal static IReadOnlyList<GpJobDefinitionTier> DefaultTiers { get; } =
    [
        GpJobDefinitionTier.S,
        GpJobDefinitionTier.M,
        GpJobDefinitionTier.L,
        GpJobDefinitionTier.Xl
    ];

    /// <summary>A conservative default per-env substrate (x86_64 Fargate-Spot, all four tiers).</summary>
    internal static GpSubstrateConfig Default { get; } = new();

    /// <summary>The pooled tiers this substrate provisions (defaults to the full s/m/l/xl pool).</summary>
    internal IReadOnlyList<GpJobDefinitionTier> EffectiveTiers => Tiers is { Count: > 0 } ? Tiers : DefaultTiers;

    /// <summary>
    /// Render the per-ENV substrate <c>-var</c> inputs for the GP substrate stack. These are
    /// substrate-shaped (enable flag, shared image/arch, compute-env ceiling, workload id,
    /// ECR flag) — NOT a per-job profile. Always sets the substrate gate on.
    ///
    /// CONTRACT: every emitted variable name MUST be a variable honua-iac actually declares
    /// (modules/aws-serverless/variables.tf + examples/aws-cert/variables.tf). The tier POOL
    /// is NOT operator-controllable: honua-iac hardcodes the four ephemeral-storage tiers via a
    /// <c>for_each</c> over <c>local.gp_batch_tiers</c> (batch.tf), so there is intentionally NO
    /// tiers variable here — emitting one would fail terraform with "Value for undeclared
    /// variable". The cross-seam test in GpAdapterContractTests guards this.
    /// </summary>
    internal IReadOnlyList<GpSubstrateVar> ToSubstrateVars()
    {
        return
        [
            new GpSubstrateVar(EnableVar, "true"),
            new GpSubstrateVar(ImageVar, Image ?? string.Empty),
            new GpSubstrateVar(CpuArchitectureVar, ToTerraformArchitecture(Architecture)),
            new GpSubstrateVar(MaxVcpusVar, MaxVcpus.ToString(CultureInfo.InvariantCulture)),
            new GpSubstrateVar(WorkloadIdVar, WorkloadId),
            new GpSubstrateVar(CreateWorkerGdalRepoVar, CreateWorkerGdalRepo ? "true" : "false")
        ];
    }

    // Per-ENV substrate input variable names. These are the EXACT names honua-iac declares
    // (modules/aws-serverless/variables.tf + examples/aws-cert/variables.tf, origin/trunk). The
    // adapter does NOT bind its post-provision behaviour to input-variable names — it binds to
    // the substrate OUTPUTS (see GpSubstrateOutputs). Per-job knobs (vcpus/memory/timeout/retry)
    // are deliberately ABSENT: those are SubmitJob overrides, never terraform inputs. The tier
    // POOL is hardcoded in honua-iac (for_each over local.gp_batch_tiers in batch.tf) and is NOT
    // a variable — so there is deliberately no tiers var. Drift is guarded by GpAdapterContractTests.
    internal const string EnableVar = "enable_gp_batch";
    internal const string ImageVar = "gp_batch_image";
    internal const string CpuArchitectureVar = "gp_batch_cpu_architecture";
    internal const string MaxVcpusVar = "gp_batch_max_vcpus";
    internal const string WorkloadIdVar = "gp_batch_workload_id";
    internal const string CreateWorkerGdalRepoVar = "create_worker_gdal_repo";

    internal static string ToTerraformArchitecture(GpCpuArchitecture architecture) => architecture switch
    {
        GpCpuArchitecture.X86_64 => "X86_64",
        GpCpuArchitecture.Arm64 => "ARM64",
        _ => "X86_64"
    };
}

/// <summary>One rendered per-ENV substrate terraform input (variable name + value).</summary>
internal sealed record GpSubstrateVar(string Name, string Value);

/// <summary>
/// The durable GP substrate the adapter provisions, addressed by its terraform OUTPUTS / ARNs.
/// The contract binds to OUTPUTS (finalized in honua-iac #70), NOT to input-variable names: the
/// old <c>gp_batch_*</c> variable-name coupling was brittle and backwards. The server consumes
/// these ARNs to submit jobs against the substrate (queue + tier-keyed job-definition pool).
/// </summary>
internal static class GpSubstrateOutputs
{
    /// <summary>Output: the GP AWS Batch job-queue ARN.</summary>
    internal const string JobQueueArn = "gp_job_queue_arn";

    /// <summary>Output: a map { s, m, l, xl } -&gt; job-definition ARN (the 20/50/100/200 GiB tiers).</summary>
    internal const string JobDefinitionArns = "gp_job_definition_arns";

    /// <summary>Output: the GP AWS Batch compute-environment ARN (Fargate-Spot).</summary>
    internal const string ComputeEnvironmentArn = "gp_compute_environment_arn";

    /// <summary>Output: the IAM role ARN the GP job container assumes.</summary>
    internal const string JobRoleArn = "gp_job_role_arn";

    /// <summary>Output: the IAM execution-role ARN AWS Batch uses to launch the task.</summary>
    internal const string ExecutionRoleArn = "gp_execution_role_arn";

    /// <summary>Output: the ECR repository URL for the GDAL worker image.</summary>
    internal const string WorkerGdalRepositoryUrl = "gp_worker_gdal_repository_url";

    /// <summary>The full ordered set of substrate outputs the adapter binds to and verifies.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        JobQueueArn,
        JobDefinitionArns,
        ComputeEnvironmentArn,
        JobRoleArn,
        ExecutionRoleArn,
        WorkerGdalRepositoryUrl
    ];

    /// <summary>The map-output JSON-path expression for one tier's job-definition ARN, e.g. <c>gp_job_definition_arns.m</c>.</summary>
    internal static string JobDefinitionArnForTier(GpJobDefinitionTier tier)
        => $"{JobDefinitionArns}.{GpResourceProfile.TierToken(tier)}";
}
