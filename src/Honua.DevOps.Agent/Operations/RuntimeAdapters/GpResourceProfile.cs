namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// CPU architecture for a geoprocessing (GP) Batch task. Mirrors the honua-iac
/// <c>gp_batch_cpu_architecture</c> variable on <c>modules/aws-serverless</c>
/// (validated to <c>X86_64</c> | <c>ARM64</c>). Templated into the job definition
/// because AWS Batch <c>SubmitJob</c> cannot override the runtime platform.
/// </summary>
internal enum GpCpuArchitecture
{
    X86_64,
    Arm64
}

/// <summary>
/// Typed geoprocessing job resource profile: the sizing envelope a GP job
/// (designed in console studio / validated by the agent) carries so the
/// <c>gp</c> runtime adapter can mint a UNIQUE per-job AWS Batch job definition.
///
/// CRITICAL nuance (honua-iac #70 / honua-server #2165/#2166): the server's
/// <c>AwsBatchComputeBackend</c> already passes vCPU / memory / timeout / retry /
/// GPU-count as per-job <c>SubmitJob</c> ContainerOverrides + resourceRequirements,
/// so those are job-definition DEFAULTS. The knobs terraform MUST template per job
/// (because SubmitJob cannot override them) are the container <see cref="Image"/>,
/// the CPU <see cref="Architecture"/>, the <see cref="EphemeralStorageGib"/> (Fargate
/// task scratch), and — on an EC2 compute-env only — the <see cref="GpuCount"/>.
/// </summary>
internal sealed record GpResourceProfile(
    int Vcpus,
    int MemoryMib,
    int GpuCount,
    int TimeoutSeconds,
    GpCpuArchitecture Architecture,
    string? Image = null,
    int? EphemeralStorageGib = null,
    int RetryAttempts = 1,
    bool CreateWorkerGdalRepo = false,
    string WorkloadId = "geoprocessing-batch")
{
    /// <summary>
    /// Compute-environment platform the profile targets. The default locked path is
    /// Fargate Spot (no GPU); an EC2 compute-env is required for any GPU profile.
    /// </summary>
    internal enum ComputePlatform
    {
        FargateSpot,
        Ec2
    }

    /// <summary>
    /// A conservative, valid Fargate-Spot baseline (1 vCPU / 2048 MiB, no GPU,
    /// 1h timeout, x86_64) matching the honua-iac job-definition defaults.
    /// </summary>
    internal static GpResourceProfile FargateBaseline { get; } = new(
        Vcpus: 1,
        MemoryMib: 2048,
        GpuCount: 0,
        TimeoutSeconds: 3600,
        Architecture: GpCpuArchitecture.X86_64);

    /// <summary>
    /// Validate the profile against the AWS Batch / honua-iac constraints and the
    /// requested <paramref name="platform"/>. Rejects a GPU profile on Fargate-Spot
    /// with a clear, actionable message (GPU requires an EC2 Batch compute-env;
    /// Fargate rejects GPU resource requirements). The bounds mirror the honua-iac
    /// variable validations exactly so a profile that passes here also passes
    /// terraform plan.
    /// </summary>
    internal GpProfileValidation Validate(ComputePlatform platform)
    {
        List<string> errors = [];

        if (Vcpus <= 0)
        {
            errors.Add("vcpus must be greater than 0.");
        }

        if (MemoryMib <= 0)
        {
            errors.Add("memory_mib must be greater than 0.");
        }

        if (TimeoutSeconds <= 0)
        {
            errors.Add("timeout_seconds must be greater than 0.");
        }

        // Mirrors honua-iac gp_batch_retry_attempts validation (1..10, AWS Batch limit).
        if (RetryAttempts < 1 || RetryAttempts > 10)
        {
            errors.Add("retry_attempts must be between 1 and 10 (AWS Batch limit).");
        }

        // Mirrors honua-iac gp_batch_gpu_count validation (0..16).
        if (GpuCount < 0 || GpuCount > 16)
        {
            errors.Add("gpu_count must be between 0 and 16.");
        }

        // Mirrors honua-iac gp_batch_ephemeral_storage_gib validation
        // (null OR 21..200; Fargate only honors values above its 20 GiB default).
        if (EphemeralStorageGib is { } ephemeral && (ephemeral < 21 || ephemeral > 200))
        {
            errors.Add("ephemeral_storage_gib must be null or between 21 and 200 GiB (Fargate ceiling is 200; values <=20 are the unmanaged default).");
        }

        // The keystone safety check: Fargate/Fargate-Spot rejects GPU resource
        // requirements, so a GPU profile on the locked Fargate path is invalid and
        // would fail at apply. GPU needs an EC2 Batch compute environment.
        if (GpuCount > 0 && platform == ComputePlatform.FargateSpot)
        {
            errors.Add(
                "gpu_count must be 0 on the Fargate-Spot compute platform: AWS Fargate rejects GPU resource requirements. " +
                "GPU geoprocessing requires an EC2 AWS Batch compute environment (provision an EC2 compute-env and target ComputePlatform.Ec2).");
        }

        return new GpProfileValidation(errors.Count == 0, errors);
    }
}

/// <summary>Result of validating a <see cref="GpResourceProfile"/>.</summary>
internal sealed record GpProfileValidation(bool IsValid, IReadOnlyList<string> Errors);
