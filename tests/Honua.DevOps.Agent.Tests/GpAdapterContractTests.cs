using System.Globalization;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// CROSS-SEAM CONTRACT GUARD (root-cause fix for the var/key drift an independent review found).
///
/// The GP runtime adapter renders two cross-repo contracts that nothing in this repo could
/// previously validate, because each side only ever asserted ITS OWN spelling:
///
///   1. <see cref="GpSubstrateConfig.ToSubstrateVars"/> renders terraform <c>-var</c> NAMES that
///      MUST exist as declared variables in honua-iac. An unknown name fails terraform with
///      "Value for undeclared variable" and silently never sets the intended input.
///   2. <see cref="GpResourceProfile.ToSizingHint"/> emits loose <c>batch.*</c> override KEYS that
///      the honua-server AWS Batch backend reads. A mis-cased key is silently dropped (the server
///      never sees the override), so the job runs with defaults.
///
/// honua-iac / honua-server are not referenced by this repo, so the authoritative names are
/// encoded here as checked-in fixtures with source pointers. If the adapter ever emits a name/key
/// outside these allow-lists (or stops emitting a required one), THIS test fails loudly — the
/// drift no longer reaches production undetected.
/// </summary>
public sealed class GpAdapterContractTests
{
    // ---------------------------------------------------------------------------------------------
    // FIXTURE 1 — honua-iac substrate INPUT VARIABLES the GP adapter may set.
    // Source (origin/trunk):
    //   honua-iac/infrastructure/terraform/modules/aws-serverless/variables.tf
    //   honua-iac/infrastructure/terraform/examples/aws-cert/variables.tf  (+ main.tf passthrough)
    // The tier POOL is HARDCODED in honua-iac (for_each over local.gp_batch_tiers in batch.tf) and
    // is intentionally NOT a variable, so there is deliberately no tiers var in this set.
    // ---------------------------------------------------------------------------------------------
    private static readonly IReadOnlySet<string> HonuaIacDeclaredGpVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "enable_gp_batch",
        "gp_batch_image",
        "gp_batch_cpu_architecture",
        "gp_batch_max_vcpus",
        "gp_batch_workload_id",
        "gp_batch_workload_name",
        "gp_batch_data_bucket_arn",
        "create_worker_gdal_repo",
    };

    // A variable name an earlier adapter version emitted that honua-iac has NEVER declared. Kept as
    // a negative fixture so a regression to the old spelling fails here, not in a terraform apply.
    private static readonly IReadOnlyList<string> RetiredUndeclaredVariableNames =
    [
        "enable_gp_substrate",
        "gp_worker_image",
        "gp_cpu_architecture",
        "gp_compute_max_vcpus",
        "gp_job_definition_tiers", // hardcoded pool in honua-iac — never a variable
        "gp_workload_id",
    ];

    // ---------------------------------------------------------------------------------------------
    // FIXTURE 2 — honua-server batch.* override KEYS the AWS Batch backend reads.
    // Source: honua-server/src/Honua.Aws/Features/ControlPlane/AwsBatchComputeBackend.cs
    //   (static class AwsBatchParameterKeys; tier-selection on feat/gp-batch-tier-selection).
    // batch.ephemeral_gib DRIVES server-side job-definition tier selection (the one sizing
    // dimension AWS Batch SubmitJob cannot override), so the adapter MUST emit it.
    // ---------------------------------------------------------------------------------------------
    private static readonly IReadOnlySet<string> HonuaServerBatchOverrideKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "batch.vcpus",
        "batch.memory_mib",
        "batch.timeout_seconds",
        "batch.retry_attempts",
        "batch.gpu_count",
        "batch.ephemeral_gib",
    };

    private static readonly IReadOnlyList<string> RequiredBatchOverrideKeys =
    [
        "batch.vcpus",
        "batch.memory_mib",
        "batch.timeout_seconds",
        "batch.retry_attempts",
        "batch.ephemeral_gib",
    ];

    // camelCase spellings the server NEVER reads (the latent defect). A regression re-introduces
    // one of these; it must fail here.
    private static readonly IReadOnlyList<string> RetiredCamelCaseOverrideKeys =
    [
        "batch.memoryMib",
        "batch.timeoutSeconds",
        "batch.retryAttempts",
        "batch.ephemeralStorageGib",
    ];

    // --- DEFECT 1 guard: every emitted substrate var must be a real honua-iac variable -----------

    // Internal adapter records cannot appear in a public [Theory] signature, so the matrix of
    // substrate configs is built inside the test rather than via MemberData.
    private static IReadOnlyList<GpSubstrateConfig> SubstrateConfigs() =>
    [
        GpSubstrateConfig.Default,
        new GpSubstrateConfig(
            Image: "123456789012.dkr.ecr.us-east-1.amazonaws.com/worker-gdal:latest",
            Architecture: GpCpuArchitecture.Arm64,
            MaxVcpus: 512,
            CreateWorkerGdalRepo: false,
            WorkloadId: "gp-bigmem",
            Tiers: [GpJobDefinitionTier.M, GpJobDefinitionTier.L, GpJobDefinitionTier.Xl]),
    ];

    [Fact]
    public void ToSubstrateVars_EmitsOnlyHonuaIacDeclaredVariables()
    {
        foreach (GpSubstrateConfig substrate in SubstrateConfigs())
        {
            string[] unknown = substrate.ToSubstrateVars()
                .Select(v => v.Name)
                .Where(name => !HonuaIacDeclaredGpVariables.Contains(name))
                .ToArray();

            Assert.True(
                unknown.Length == 0,
                "ToSubstrateVars() emitted terraform -var names that honua-iac does NOT declare " +
                "(would fail 'Value for undeclared variable'): " + string.Join(", ", unknown) +
                ". Update the *Var constants in GpSubstrateContract.cs to real honua-iac names, or add " +
                "the new variable to honua-iac AND the HonuaIacDeclaredGpVariables fixture.");
        }
    }

    [Fact]
    public void ToSubstrateVars_NeverEmitsRetiredUndeclaredNames()
    {
        HashSet<string> emitted = GpSubstrateConfig.Default.ToSubstrateVars()
            .Select(v => v.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string retired in RetiredUndeclaredVariableNames)
        {
            Assert.DoesNotContain(retired, emitted);
        }
    }

    [Fact]
    public void ToSubstrateVars_EmitsTheSubstrateGateAndKnobs()
    {
        Dictionary<string, string> byName = GpSubstrateConfig.Default.ToSubstrateVars()
            .ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        // The substrate gate is the one variable that MUST be present and on.
        Assert.Equal("true", byName["enable_gp_batch"]);
        // Substrate knobs the adapter is responsible for setting.
        Assert.Contains("gp_batch_cpu_architecture", byName.Keys);
        Assert.Contains("gp_batch_max_vcpus", byName.Keys);
        Assert.Contains("gp_batch_workload_id", byName.Keys);
    }

    // --- DEFECT 2 guard: sizing-hint keys must be the EXACT keys the server reads -----------------

    private static IReadOnlyList<GpResourceProfile> SizingProfiles() =>
    [
        GpResourceProfile.Baseline,
        new GpResourceProfile(Vcpus: 8, MemoryMib: 32768, TimeoutSeconds: 7200, RetryAttempts: 3, EphemeralStorageGib: 100),
        new GpResourceProfile(Vcpus: 2, MemoryMib: 4096, TimeoutSeconds: 1800, RetryAttempts: 1, EphemeralStorageGib: null),
    ];

    [Fact]
    public void ToSizingHint_EmitsOnlyKeysTheServerReads()
    {
        foreach (GpResourceProfile profile in SizingProfiles())
        {
            IReadOnlyDictionary<string, string> overrides = profile.ToSizingHint().SubmitJobOverrides;

            string[] unknown = overrides.Keys
                .Where(key => !HonuaServerBatchOverrideKeys.Contains(key))
                .ToArray();

            Assert.True(
                unknown.Length == 0,
                "ToSizingHint() emitted batch.* override keys the honua-server AWS Batch backend does " +
                "NOT read (silently dropped at SubmitJob): " + string.Join(", ", unknown) +
                ". Match AwsBatchParameterKeys in honua-server, or add the new key to that backend AND " +
                "the HonuaServerBatchOverrideKeys fixture.");
        }
    }

    [Fact]
    public void ToSizingHint_EmitsAllRequiredServerKeys()
    {
        foreach (GpResourceProfile profile in SizingProfiles())
        {
            IReadOnlyDictionary<string, string> overrides = profile.ToSizingHint().SubmitJobOverrides;

            foreach (string required in RequiredBatchOverrideKeys)
            {
                Assert.Contains(required, overrides.Keys);
            }
        }
    }

    [Fact]
    public void ToSizingHint_NeverEmitsRetiredCamelCaseKeys()
    {
        IReadOnlyDictionary<string, string> overrides =
            new GpResourceProfile(Vcpus: 8, MemoryMib: 32768, TimeoutSeconds: 7200, RetryAttempts: 3, EphemeralStorageGib: 100)
                .ToSizingHint().SubmitJobOverrides;

        foreach (string retired in RetiredCamelCaseOverrideKeys)
        {
            Assert.DoesNotContain(retired, overrides.Keys);
        }
    }

    [Fact]
    public void ToSizingHint_EphemeralGib_DrivesTierAndIsEmitted()
    {
        // The ephemeral need the server reads for tier selection MUST equal the value that picks
        // the tier here, so the adapter's tier hint and the server's selection agree.
        GpResourceProfile profile = new(Vcpus: 4, MemoryMib: 16384, TimeoutSeconds: 3600, EphemeralStorageGib: 100);

        GpSizingHint hint = profile.ToSizingHint();

        Assert.Equal("l", hint.TierToken);
        Assert.Equal("100", hint.SubmitJobOverrides["batch.ephemeral_gib"]);

        // A null/unset ephemeral need falls back to the smallest tier's 20 GiB on BOTH sides.
        GpSizingHint unset = GpResourceProfile.Baseline.ToSizingHint();
        Assert.Equal("s", unset.TierToken);
        Assert.Equal(
            GpResourceProfile.TierSmallCeilingGib.ToString(CultureInfo.InvariantCulture),
            unset.SubmitJobOverrides["batch.ephemeral_gib"]);
    }
}
