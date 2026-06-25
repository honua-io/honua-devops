using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// CROSS-SEAM CONTRACT GUARD for the AZURE GP runtime adapter (mirrors GpAdapterContractTests, the
/// root-cause fix for the AWS adapter's #108 var/key drift).
///
/// The Azure GP adapter renders two cross-repo contracts that nothing else in this repo can
/// validate, because each side only ever asserts ITS OWN spelling:
///
///   1. <see cref="AzureGpSubstrateConfig.ToSubstrateVars"/> renders terraform <c>-var</c> NAMES
///      that MUST exist as declared variables in the honua-iac <c>azure-gp</c> module. An unknown
///      name fails terraform with "Value for undeclared variable" and silently never sets the
///      intended input.
///   2. <see cref="AzureGpResourceProfile.ToSizingHint"/> emits loose <c>azure.batch.*</c> override
///      KEYS that the honua-server <c>AzureBatchComputeBackend</c> reads. A mis-spelled key is
///      silently dropped (the server never sees the override), so the task runs with defaults.
///
/// honua-iac / honua-server are not referenced by this repo, so the authoritative names are
/// encoded here as checked-in fixtures with source pointers. If the adapter ever emits a name/key
/// outside these allow-lists (or stops emitting a required one), THIS test fails loudly — the
/// drift no longer reaches production undetected.
/// </summary>
public sealed class AzureGpAdapterContractTests
{
    // ---------------------------------------------------------------------------------------------
    // FIXTURE 1 — honua-iac azure-cert substrate PASSTHROUGH variables the Azure GP adapter may set.
    // Source (branch feat/azure-gp-substrate — VERIFIED against the pushed branch):
    //   honua-iac/infrastructure/terraform/examples/azure-cert/variables.tf  (+ main.tf passthrough)
    //   honua-iac/infrastructure/terraform/modules/azure-gp/variables.tf
    // The adapter applies terraform at examples/azure-cert; that stack HARDCODES
    // enable_azure_gp_substrate = true (it is NOT a passthrough var) and exposes only the pool VM
    // size, node ceiling, and create-ACR flag as GP passthrough inputs (plus gp_pool_use_low_priority
    // / worker_gdal_acr_sku, which the adapter leaves at their module defaults). The single worker
    // POOL is the MVP substrate shape; there is intentionally NO pool-tier variable (Azure sizes
    // per-pool and the MVP is one pool — the tier selector is the deferred fast-follow once the iac
    // adds multiple pools). The worker image is a structured module input (gp_pool_image_reference)
    // and the ACR name is module-derived, so neither is an azure-cert passthrough -var.
    // ---------------------------------------------------------------------------------------------
    private static readonly IReadOnlySet<string> HonuaIacDeclaredAzureGpVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "gp_pool_vm_size",
        "gp_pool_max_nodes",
        "gp_pool_use_low_priority",
        "create_worker_gdal_acr",
        "worker_gdal_acr_sku",
    };

    // Names a multi-pool / AWS-shaped variant, or the pre-verification guessed contract, might emit
    // that the azure-cert stack does NOT declare as a passthrough. Kept as a negative fixture so a
    // regression fails here, not in a terraform apply ("Value for undeclared variable").
    private static readonly IReadOnlyList<string> RetiredUndeclaredVariableNames =
    [
        "enable_azure_gp_substrate", // hardcoded in the azure-cert stack, NOT a passthrough -var
        "gp_worker_image",           // image is a structured module input (gp_pool_image_reference)
        "gp_acr_name",               // ACR name is module-derived, not a passthrough var
        "gp_workload_id",            // server-side registration, not an iac variable
        "gp_max_nodes",              // wrong name — the real var is gp_pool_max_nodes
        "gp_pool_tiers",             // multi-pool tier selection — deferred fast-follow, not a var yet
        "gp_job_definition_tiers",   // AWS-shaped tier pool — no Azure analogue
        "enable_gp_batch",           // AWS substrate gate
        "gp_batch_cpu_architecture",
        "gp_batch_max_vcpus",
    ];

    // ---------------------------------------------------------------------------------------------
    // FIXTURE 2 — honua-server azure.batch.* / azure.storage.* override KEYS the backend reads.
    // Source: honua-server/src/Honua.Server/Features/ControlPlane/AzureBatchComputeBackend.cs
    //   (private const Param* fields; origin/trunk). The backend reads exactly:
    //     azure.batch.account_url, azure.batch.pool_id, azure.batch.container_image,
    //     azure.batch.container_run_options, azure.batch.command_line,
    //     azure.batch.max_task_retry_count, azure.batch.task_timeout_minutes,
    //     azure.storage.output_container_url, and the azure.batch.env. prefix.
    // The adapter's runtime sizing emits the per-task SUBSET it actually sets (pool_id +
    // task_timeout_minutes + max_task_retry_count + optional container_image); the others are
    // substrate-OUTPUT-sourced (account_url, output_container_url) or invariant (command_line),
    // not runtime overrides. The allow-list is the full server-read set so any emitted key is valid.
    // ---------------------------------------------------------------------------------------------
    private static readonly IReadOnlySet<string> HonuaServerAzureBatchKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "azure.batch.account_url",
        "azure.batch.pool_id",
        "azure.batch.container_image",
        "azure.batch.container_run_options",
        "azure.batch.command_line",
        "azure.batch.max_task_retry_count",
        "azure.batch.task_timeout_minutes",
        "azure.storage.output_container_url",
    };

    // The per-task override keys the runtime sizing MUST always emit (pool_id is conditional on the
    // substrate output being resolved, so it is asserted separately).
    private static readonly IReadOnlyList<string> RequiredAzureBatchOverrideKeys =
    [
        "azure.batch.task_timeout_minutes",
        "azure.batch.max_task_retry_count",
    ];

    // camelCase / AWS-shaped spellings the Azure server NEVER reads. A regression re-introduces one
    // of these; it must fail here.
    private static readonly IReadOnlyList<string> RetiredWrongKeys =
    [
        "azure.batch.taskTimeoutMinutes",
        "azure.batch.maxTaskRetryCount",
        "azure.batch.poolId",
        "batch.timeout_seconds",   // AWS key
        "batch.retry_attempts",    // AWS key
        "batch.vcpus",             // AWS key — Azure has no per-task vCPU override (per-pool sizing)
    ];

    // --- The substrate OUTPUT names the adapter binds to (the honua-iac azure-gp module outputs).
    // These are documented in the task contract; binding to OUTPUTS (never input-var names) is the
    // lesson the AWS adapter learned in #108.
    private static readonly IReadOnlySet<string> ExpectedSubstrateOutputs = new HashSet<string>(StringComparer.Ordinal)
    {
        "gp_batch_account_url",
        "gp_pool_id",
        "gp_batch_account_id",
        "gp_task_identity_id",
        "gp_task_identity_principal_id",
        "gp_acr_login_server",
        "gp_output_container_url",
        "gp_control_plane_backend_name",
    };

    // --- DEFECT-1 guard: every emitted substrate var must be a real azure-gp module variable ------

    private static IReadOnlyList<AzureGpSubstrateConfig> SubstrateConfigs() =>
    [
        AzureGpSubstrateConfig.Default,
        new AzureGpSubstrateConfig(
            Image: "myacr.azurecr.io/worker-gdal:latest",
            AcrName: "myacr",
            PoolVmSize: "Standard_D8s_v5",
            MaxNodes: 32,
            WorkloadId: "gp-bigmem",
            CreateWorkerGdalAcr: false),
    ];

    [Fact]
    public void ToSubstrateVars_EmitsOnlyHonuaIacDeclaredVariables()
    {
        foreach (AzureGpSubstrateConfig substrate in SubstrateConfigs())
        {
            string[] unknown = substrate.ToSubstrateVars()
                .Select(v => v.Name)
                .Where(name => !HonuaIacDeclaredAzureGpVariables.Contains(name))
                .ToArray();

            Assert.True(
                unknown.Length == 0,
                "ToSubstrateVars() emitted terraform -var names that the honua-iac azure-gp module does " +
                "NOT declare (would fail 'Value for undeclared variable'): " + string.Join(", ", unknown) +
                ". Update the *Var constants in AzureGpSubstrateContract.cs to real azure-gp names, or add " +
                "the new variable to honua-iac AND the HonuaIacDeclaredAzureGpVariables fixture.");
        }
    }

    [Fact]
    public void ToSubstrateVars_NeverEmitsRetiredOrAwsShapedNames()
    {
        HashSet<string> emitted = AzureGpSubstrateConfig.Default.ToSubstrateVars()
            .Select(v => v.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string retired in RetiredUndeclaredVariableNames)
        {
            Assert.DoesNotContain(retired, emitted);
        }
    }

    [Fact]
    public void ToSubstrateVars_EmitsTheSubstrateKnobs()
    {
        Dictionary<string, string> byName = AzureGpSubstrateConfig.Default.ToSubstrateVars()
            .ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        // Substrate knobs the adapter is responsible for setting at the azure-cert stack
        // (single-pool shape). The feature gate is hardcoded inside the stack, NOT a passthrough.
        Assert.Contains("gp_pool_vm_size", byName.Keys);
        Assert.Contains("gp_pool_max_nodes", byName.Keys);
        Assert.Contains("create_worker_gdal_acr", byName.Keys);
        // The gate is not a passthrough var (the stack hardcodes enable_azure_gp_substrate=true).
        Assert.DoesNotContain("enable_azure_gp_substrate", byName.Keys);
        // No pool-tier variable: single-pool MVP.
        Assert.DoesNotContain("gp_pool_tiers", byName.Keys);
    }

    // --- DEFECT-2 guard: sizing-hint keys must be the EXACT keys the server reads ------------------

    private static IReadOnlyList<(AzureGpResourceProfile Profile, string? PoolId)> SizingProfiles() =>
    [
        (AzureGpResourceProfile.Baseline, null),
        (AzureGpResourceProfile.Baseline, "honua-gp-pool"),
        (new AzureGpResourceProfile(TaskTimeoutMinutes: 240, MaxTaskRetryCount: 3, ContainerImage: "myacr.azurecr.io/worker-gdal:v2"), "honua-gp-pool"),
    ];

    [Fact]
    public void ToSizingHint_EmitsOnlyKeysTheServerReads()
    {
        foreach ((AzureGpResourceProfile profile, string? poolId) in SizingProfiles())
        {
            IReadOnlyDictionary<string, string> overrides = profile.ToSizingHint(poolId).SubmitTaskOverrides;

            string[] unknown = overrides.Keys
                .Where(key => !HonuaServerAzureBatchKeys.Contains(key))
                .ToArray();

            Assert.True(
                unknown.Length == 0,
                "ToSizingHint() emitted azure.batch.* override keys the honua-server AzureBatchComputeBackend " +
                "does NOT read (silently dropped at task-add): " + string.Join(", ", unknown) +
                ". Match the Param* fields in AzureBatchComputeBackend.cs, or add the new key to that backend " +
                "AND the HonuaServerAzureBatchKeys fixture.");
        }
    }

    [Fact]
    public void ToSizingHint_EmitsAllRequiredServerKeys()
    {
        foreach ((AzureGpResourceProfile profile, string? poolId) in SizingProfiles())
        {
            IReadOnlyDictionary<string, string> overrides = profile.ToSizingHint(poolId).SubmitTaskOverrides;

            foreach (string required in RequiredAzureBatchOverrideKeys)
            {
                Assert.Contains(required, overrides.Keys);
            }
        }
    }

    [Fact]
    public void ToSizingHint_NeverEmitsRetiredOrWrongCaseKeys()
    {
        IReadOnlyDictionary<string, string> overrides =
            new AzureGpResourceProfile(TaskTimeoutMinutes: 120, MaxTaskRetryCount: 2, ContainerImage: "myacr.azurecr.io/worker-gdal:v2")
                .ToSizingHint("honua-gp-pool").SubmitTaskOverrides;

        foreach (string retired in RetiredWrongKeys)
        {
            Assert.DoesNotContain(retired, overrides.Keys);
        }
    }

    [Fact]
    public void ToSizingHint_PoolId_IsEmittedWhenResolved_AndOmittedWhenNot()
    {
        // When the substrate output is resolved, the single-pool constant is set under the exact
        // server key azure.batch.pool_id (no tier selection — single-pool MVP).
        IReadOnlyDictionary<string, string> withPool =
            AzureGpResourceProfile.Baseline.ToSizingHint("honua-gp-pool").SubmitTaskOverrides;
        Assert.Equal("honua-gp-pool", withPool["azure.batch.pool_id"]);

        // When not resolved, pool_id is omitted (the server falls back to its default pool) and the
        // hint surfaces a note steering the operator to the gp_pool_id output.
        AzureGpSizingHint unset = AzureGpResourceProfile.Baseline.ToSizingHint(poolId: null);
        Assert.DoesNotContain("azure.batch.pool_id", unset.SubmitTaskOverrides.Keys);
        Assert.Contains(unset.Notes, n => n.Contains("gp_pool_id", StringComparison.Ordinal));
    }

    // --- OUTPUT-binding guard: the adapter binds to the azure-gp module OUTPUTS, not input vars ----

    [Fact]
    public void SubstrateOutputs_MatchTheAzureGpModuleOutputContract()
    {
        Assert.Equal(
            ExpectedSubstrateOutputs.OrderBy(x => x, StringComparer.Ordinal),
            AzureGpSubstrateOutputs.All.OrderBy(x => x, StringComparer.Ordinal));

        // The control-plane backend name output MUST carry the honua-server backend identifier so
        // the server routes GP tasks to AzureBatchComputeBackend.
        Assert.Equal("honua-azure-batch", AzureGpSubstrateOutputs.ControlPlaneBackendIdentifier);
        Assert.Equal("gp_control_plane_backend_name", AzureGpSubstrateOutputs.ControlPlaneBackendName);

        // No substrate output name overlaps an INPUT variable name (the brittle coupling #108 removed).
        foreach (string output in AzureGpSubstrateOutputs.All)
        {
            Assert.DoesNotContain(output, HonuaIacDeclaredAzureGpVariables);
        }
    }
}
