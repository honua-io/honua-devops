namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// One templated terraform input for the per-job GP Batch provision: the exact
/// honua-iac <c>modules/aws-serverless</c> variable name and its rendered value.
/// </summary>
internal sealed record GpBatchTerraformVar(string Name, string Value);

/// <summary>
/// Maps a <see cref="GpResourceProfile"/> to the honua-iac <c>gp_batch_*</c>
/// terraform variables on <c>modules/aws-serverless</c> (gated by
/// <c>enable_gp_batch=true</c>, instantiated by <c>examples/aws-cert</c>).
///
/// The variable NAMES here are bound 1:1 to honua-iac PR #70
/// (<c>infrastructure/terraform/modules/aws-serverless/variables.tf</c>) and must
/// stay in lockstep with that interface. vCPU / memory / timeout / retry / GPU are
/// emitted as job-definition DEFAULTS — the server's AwsBatchComputeBackend overrides
/// them per job at SubmitJob time — while image / arch / ephemeral-storage (and GPU on
/// an EC2 compute-env) are the UNIQUE-per-job knobs SubmitJob cannot override, which is
/// why the agent re-applies terraform with a per-job profile to mint a sized job def.
/// </summary>
internal static class GpBatchTerraformMapping
{
    // honua-iac modules/aws-serverless variable names — keep in lockstep with PR #70.
    internal const string EnableVar = "enable_gp_batch";
    internal const string ImageVar = "gp_batch_image";
    internal const string VcpusVar = "gp_batch_vcpus";
    internal const string MemoryMibVar = "gp_batch_memory_mib";
    internal const string CpuArchitectureVar = "gp_batch_cpu_architecture";
    internal const string GpuCountVar = "gp_batch_gpu_count";
    internal const string EphemeralStorageGibVar = "gp_batch_ephemeral_storage_gib";
    internal const string TimeoutSecondsVar = "gp_batch_timeout_seconds";
    internal const string RetryAttemptsVar = "gp_batch_retry_attempts";
    internal const string WorkloadIdVar = "gp_batch_workload_id";
    internal const string CreateWorkerGdalRepoVar = "create_worker_gdal_repo";

    /// <summary>
    /// Render the ordered list of <c>-var</c> inputs for a GP per-job Batch provision.
    /// Always sets <c>enable_gp_batch=true</c> (the module gate). The ephemeral-storage
    /// var is emitted as <c>null</c> when the profile leaves it unset (Fargate's 20 GiB
    /// default), matching the honua-iac variable default.
    /// </summary>
    internal static IReadOnlyList<GpBatchTerraformVar> ToTerraformVars(GpResourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return
        [
            new GpBatchTerraformVar(EnableVar, "true"),
            new GpBatchTerraformVar(ImageVar, profile.Image ?? string.Empty),
            new GpBatchTerraformVar(VcpusVar, profile.Vcpus.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new GpBatchTerraformVar(MemoryMibVar, profile.MemoryMib.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new GpBatchTerraformVar(CpuArchitectureVar, ToTerraformArchitecture(profile.Architecture)),
            new GpBatchTerraformVar(GpuCountVar, profile.GpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new GpBatchTerraformVar(
                EphemeralStorageGibVar,
                profile.EphemeralStorageGib is { } gib
                    ? gib.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "null"),
            new GpBatchTerraformVar(TimeoutSecondsVar, profile.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new GpBatchTerraformVar(RetryAttemptsVar, profile.RetryAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new GpBatchTerraformVar(WorkloadIdVar, profile.WorkloadId),
            new GpBatchTerraformVar(CreateWorkerGdalRepoVar, profile.CreateWorkerGdalRepo ? "true" : "false")
        ];
    }

    /// <summary>
    /// honua-iac <c>gp_batch_cpu_architecture</c> accepts the uppercase AWS tokens
    /// <c>X86_64</c> | <c>ARM64</c> (validated by the module). Render the enum to those.
    /// </summary>
    internal static string ToTerraformArchitecture(GpCpuArchitecture architecture) => architecture switch
    {
        GpCpuArchitecture.X86_64 => "X86_64",
        GpCpuArchitecture.Arm64 => "ARM64",
        _ => "X86_64"
    };
}
