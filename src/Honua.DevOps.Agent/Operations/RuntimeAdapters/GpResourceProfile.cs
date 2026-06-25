using System.Globalization;

namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// CPU architecture for the geoprocessing (GP) Batch substrate. Mirrors the honua-iac
/// substrate input that selects the runtime platform of the pooled job-definition tiers
/// (validated to <c>X86_64</c> | <c>ARM64</c>). This is a SUBSTRATE-level choice (the whole
/// tier pool shares one architecture), not a per-job knob: AWS Batch <c>SubmitJob</c> cannot
/// override the runtime platform.
/// </summary>
internal enum GpCpuArchitecture
{
    X86_64,
    Arm64
}

/// <summary>
/// The four durable GP job-definition ephemeral-storage tiers the substrate provisions as a
/// POOL. Each tier is a pre-registered AWS Batch job definition with a fixed Fargate task
/// scratch size; the server selects a tier per job at <c>SubmitJob</c> time. Ephemeral storage
/// is the only sizing dimension that CANNOT be overridden per job, so it is baked into the
/// tier rather than provisioned per job.
/// </summary>
internal enum GpJobDefinitionTier
{
    /// <summary>Small: up to 20 GiB ephemeral storage (Fargate default).</summary>
    S,

    /// <summary>Medium: up to 50 GiB ephemeral storage.</summary>
    M,

    /// <summary>Large: up to 100 GiB ephemeral storage.</summary>
    L,

    /// <summary>Extra-large: up to 200 GiB ephemeral storage (Fargate ceiling).</summary>
    Xl
}

/// <summary>
/// A pure RUNTIME SIZING HINT for a single geoprocessing job. Given a job's resource needs
/// (esp. ephemeral-storage GiB), this selects the durable job-definition <see cref="GpJobDefinitionTier"/>
/// from the substrate's pre-provisioned pool and produces the AWS Batch <c>SubmitJob</c>
/// overrides (vCPU / memory / timeout / retry) the server consumes as loose <c>batch.*</c>
/// parameters.
///
/// CRITICAL design (corrected per honua-iac #70 / honua-server #2165/#2166): NONE of this
/// touches terraform. The durable substrate (compute-env, queue, IAM, ECR, and the tier pool)
/// is provisioned ONCE per environment by <see cref="GpRuntimeAdapter"/>; per-job sizing is a
/// pure function the server applies at <c>SubmitJob</c> time with ContainerOverrides +
/// resourceRequirements + a job-definition tier selection. There is NO per-job terraform run,
/// NO per-job agent invocation, and NO state-lock contention.
/// </summary>
internal sealed record GpResourceProfile(
    int Vcpus,
    int MemoryMib,
    int TimeoutSeconds,
    int RetryAttempts = 1,
    int? EphemeralStorageGib = null,
    int GpuCount = 0,
    string WorkloadId = "geoprocessing-batch")
{
    /// <summary>The four ephemeral-storage tier ceilings (GiB) the substrate pool exposes.</summary>
    internal const int TierSmallCeilingGib = 20;
    internal const int TierMediumCeilingGib = 50;
    internal const int TierLargeCeilingGib = 100;
    internal const int TierXlCeilingGib = 200;

    /// <summary>
    /// A conservative, valid Fargate-Spot baseline (1 vCPU / 2048 MiB, 1h timeout). Used as the
    /// runtime default sizing hint when a job carries no explicit envelope.
    /// </summary>
    internal static GpResourceProfile Baseline { get; } = new(
        Vcpus: 1,
        MemoryMib: 2048,
        TimeoutSeconds: 3600);

    /// <summary>
    /// Pure tier-selection from a job's ephemeral-storage need. Boundaries (inclusive ceilings):
    /// <c>&lt;=20 -&gt; s</c>, <c>&lt;=50 -&gt; m</c>, <c>&lt;=100 -&gt; l</c>, <c>&lt;=200 -&gt; xl</c>.
    /// A request above 200 GiB exceeds the Fargate ceiling and the substrate pool, so it returns
    /// no tier (the caller surfaces an error). A null/unset request maps to the <c>s</c> tier
    /// (Fargate's 20 GiB default).
    /// </summary>
    internal static GpJobDefinitionTier? SelectTier(int? ephemeralStorageGib)
    {
        int gib = ephemeralStorageGib ?? TierSmallCeilingGib;
        if (gib <= 0)
        {
            return GpJobDefinitionTier.S;
        }

        return gib switch
        {
            <= TierSmallCeilingGib => GpJobDefinitionTier.S,
            <= TierMediumCeilingGib => GpJobDefinitionTier.M,
            <= TierLargeCeilingGib => GpJobDefinitionTier.L,
            <= TierXlCeilingGib => GpJobDefinitionTier.Xl,
            _ => null
        };
    }

    /// <summary>The lowercase tier token (<c>s</c>/<c>m</c>/<c>l</c>/<c>xl</c>) used as the key into the substrate's job-definition-ARN map output.</summary>
    internal static string TierToken(GpJobDefinitionTier tier) => tier switch
    {
        GpJobDefinitionTier.S => "s",
        GpJobDefinitionTier.M => "m",
        GpJobDefinitionTier.L => "l",
        GpJobDefinitionTier.Xl => "xl",
        _ => "s"
    };

    /// <summary>
    /// Produce the runtime sizing hint for this job: the selected durable job-definition tier
    /// and the <c>SubmitJob</c> overrides the server applies (NO terraform). Returns an invalid
    /// hint (with errors) if the ephemeral-storage need exceeds the 200 GiB substrate ceiling,
    /// or if vCPU / memory / timeout / retry are out of AWS Batch bounds. A GPU need is NOT a
    /// hard reject here: it is surfaced as an advisory note because GPU requires an opt-in GPU
    /// compute-env that the default Fargate-Spot substrate does not provision.
    /// </summary>
    internal GpSizingHint ToSizingHint()
    {
        List<string> errors = [];
        List<string> notes = [];

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

        // AWS Batch retry-attempts limit (1..10).
        if (RetryAttempts < 1 || RetryAttempts > 10)
        {
            errors.Add("retry_attempts must be between 1 and 10 (AWS Batch limit).");
        }

        if (EphemeralStorageGib is { } gib && gib < 0)
        {
            errors.Add("ephemeral_storage_gib must be >= 0.");
        }

        GpJobDefinitionTier? tier = SelectTier(EphemeralStorageGib);
        if (tier is null)
        {
            errors.Add(
                $"ephemeral_storage_gib={EphemeralStorageGib} exceeds the {TierXlCeilingGib} GiB Fargate ceiling and the substrate tier pool (s<=20/m<=50/l<=100/xl<=200). " +
                "Reduce the scratch footprint or provision a larger-storage substrate tier out of band.");
        }

        // GPU is advisory, not a hard reject: the default Fargate-Spot substrate has no GPU
        // tiers. Surfacing it as a note lets the planner steer to an opt-in GPU compute-env
        // without minting a broken SubmitJob override against the Fargate pool.
        if (GpuCount > 0)
        {
            notes.Add(
                $"gpu_count={GpuCount} is unsupported on the default Fargate-Spot GP substrate: AWS Fargate rejects GPU resource requirements. " +
                "GPU geoprocessing requires an opt-in EC2/GPU AWS Batch compute environment that the default substrate does not provision.");
        }

        // Loose batch.* SubmitJob overrides the server consumes. ephemeral-storage is NOT an
        // override (it is the tier selection); GPU is omitted from the Fargate override set.
        Dictionary<string, string> overrides = new(StringComparer.Ordinal)
        {
            ["batch.vcpus"] = Vcpus.ToString(CultureInfo.InvariantCulture),
            ["batch.memoryMib"] = MemoryMib.ToString(CultureInfo.InvariantCulture),
            ["batch.timeoutSeconds"] = TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["batch.retryAttempts"] = RetryAttempts.ToString(CultureInfo.InvariantCulture)
        };

        return new GpSizingHint(
            IsValid: errors.Count == 0,
            Tier: tier,
            TierToken: tier is { } selected ? TierToken(selected) : null,
            SubmitJobOverrides: overrides,
            WorkloadId: WorkloadId,
            Errors: errors,
            Notes: notes);
    }
}

/// <summary>
/// The runtime sizing hint for a single GP job: the selected durable job-definition tier and
/// the loose <c>batch.*</c> <c>SubmitJob</c> overrides the server applies. Pure data — no
/// terraform, no infra mutation.
/// </summary>
internal sealed record GpSizingHint(
    bool IsValid,
    GpJobDefinitionTier? Tier,
    string? TierToken,
    IReadOnlyDictionary<string, string> SubmitJobOverrides,
    string WorkloadId,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes);
