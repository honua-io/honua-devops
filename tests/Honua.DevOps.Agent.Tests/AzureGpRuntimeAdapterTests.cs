using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Tests;

public sealed class AzureGpRuntimeAdapterTests
{
    private static RuntimeAdapterRequest PlanRequest(bool dryRun = true) => new(
        Service: "geoprocessing",
        Environments: ["dev"],
        Revision: "HEAD",
        Action: "apply",
        ChangeSummary: "azure gp per-env substrate provision",
        GitOpsTool: "honua-gitops",
        TerraformRepository: "https://github.com/honua-io/honua-iac",
        TerraformRef: "trunk",
        TerraformLocalPath: "/tmp/honua-iac",
        DryRun: dryRun,
        ExecutionMode: dryRun ? ExecutionMode.Plan : ExecutionMode.Execute,
        ExecutionTier: dryRun ? ExecutionTier.Plan : ExecutionTier.ExecuteLowerEnv);

    [Fact]
    public void Registry_ResolvesAzureGpTarget_AsBatchJobFamily()
    {
        RuntimeAdapterCapability capability = RuntimeAdapterCatalog.Resolve("azure-gp");

        Assert.Equal("azure-gp", capability.Target);
        Assert.Equal("batch-job", capability.Family);
        Assert.True(capability.SupportsInfraPlanning);
        Assert.True(capability.SupportsInfraApply);
        Assert.True(capability.SupportsVerify);
        Assert.True(capability.SupportsRollback);
        Assert.True(capability.SupportsDrift);
        // Infra IS the deliverable: no separate release backend, no traffic shifting.
        Assert.False(capability.SupportsReleasePlanning);
        Assert.False(capability.SupportsReleaseApply);
        Assert.False(capability.SupportsTrafficShifting);
    }

    [Fact]
    public void Registry_ResolvesAzureGpAlongsideExistingTargets()
    {
        IReadOnlyList<RuntimeAdapterCapability> all = RuntimeAdapterCatalog.ResolveMany(
            ["azure-functions", "lambda", "aks", "eks", "ecs", "aca", "gp", "azure-gp"]);

        Assert.Equal(8, all.Count);
        Assert.Contains(all, c => c.Target == "azure-gp");
    }

    [Fact]
    public void Registry_UnknownTarget_ListsAzureGpAsSupported()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => RuntimeAdapterCatalog.Resolve("not-a-target"));

        Assert.Contains("azure-gp", ex.Message, StringComparison.Ordinal);
    }

    // --- Substrate provisioning (per-ENV, GitOps-gated): single-pool, NO per-job/tier inputs ---

    [Fact]
    public void Substrate_Default_RendersPerEnvVars_NoPerJobOrTierKnobs()
    {
        IReadOnlyList<AzureGpSubstrateVar> vars = AzureGpSubstrateConfig.Default.ToSubstrateVars();
        Dictionary<string, string> byName = vars.ToDictionary(v => v.Name, v => v.Value);

        // Per-env substrate passthrough inputs only — exact honua-iac azure-cert variable names.
        Assert.Equal("Standard_D4s_v5", byName["gp_pool_vm_size"]);
        Assert.Equal("8", byName["gp_pool_max_nodes"]);
        Assert.Equal("true", byName["create_worker_gdal_acr"]);

        // The feature gate is hardcoded in the azure-cert stack, NOT a passthrough -var.
        Assert.DoesNotContain("enable_azure_gp_substrate", byName.Keys);
        // Image/ACR/workload-id are not azure-cert passthrough vars (module-owned / server-side).
        Assert.DoesNotContain("gp_worker_image", byName.Keys);
        Assert.DoesNotContain("gp_acr_name", byName.Keys);
        Assert.DoesNotContain("gp_workload_id", byName.Keys);
        // Single-pool MVP: NO pool-tier variable.
        Assert.DoesNotContain("gp_pool_tiers", byName.Keys);
        // Per-job knobs are NOT terraform inputs — they are per-task overrides.
        Assert.DoesNotContain("gp_task_timeout_minutes", byName.Keys);
        Assert.DoesNotContain("gp_max_task_retry_count", byName.Keys);
    }

    [Fact]
    public void Substrate_CustomPool_RendersPoolSizeAndAcr()
    {
        AzureGpSubstrateConfig substrate = new(
            Image: "myacr.azurecr.io/worker-gdal:latest",
            AcrName: "myacr",
            PoolVmSize: "Standard_D8s_v5",
            MaxNodes: 32,
            WorkloadId: "gp-bigmem",
            CreateWorkerGdalAcr: false);

        Dictionary<string, string> byName = substrate.ToSubstrateVars().ToDictionary(v => v.Name, v => v.Value);

        // Only the azure-cert passthrough vars are rendered.
        Assert.Equal("Standard_D8s_v5", byName["gp_pool_vm_size"]);
        Assert.Equal("32", byName["gp_pool_max_nodes"]);
        Assert.Equal("false", byName["create_worker_gdal_acr"]);
        // Image/ACR/workload-id are carried on the config (proposal findings) but not as -vars.
        Assert.DoesNotContain("gp_acr_name", byName.Keys);
        Assert.DoesNotContain("gp_worker_image", byName.Keys);
    }

    [Fact]
    public void PlanInfra_EmitsTerraformPlanAtAzureCert_WithSubstrateVarFlags_AndBindsToOutputs()
    {
        AzureGpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan plan = adapter.PlanInfrastructure(PlanRequest());

        string command = Assert.Single(plan.SuggestedCommands);
        Assert.Contains("terraform -chdir", command, StringComparison.Ordinal);
        Assert.Contains("examples/azure-cert", command, StringComparison.Ordinal);
        Assert.Contains(" plan ", $" {command} ", StringComparison.Ordinal);
        // The azure-cert stack hardcodes the gate; the adapter passes the pool knobs as -vars.
        Assert.Contains("gp_pool_vm_size=", command, StringComparison.Ordinal);
        Assert.DoesNotContain("enable_azure_gp_substrate=true", command, StringComparison.Ordinal);
        // No tier var, no per-job var, no apply.
        Assert.DoesNotContain("gp_pool_tiers", command, StringComparison.Ordinal);
        Assert.DoesNotContain(" apply ", $" {command} ", StringComparison.Ordinal);

        // The plan announces it binds to the substrate OUTPUTS, not input-variable names.
        Assert.Contains(plan.ValidationChecks, c => c.Contains("gp_batch_account_url", StringComparison.Ordinal));
        Assert.Contains(plan.ValidationChecks, c => c.Contains("gp_pool_id", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyInfra_InPlanMode_DegradesToTerraformPlan_NoApply()
    {
        AzureGpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan planMode = adapter.ApplyInfrastructure(PlanRequest(dryRun: true));
        string planCommand = Assert.Single(planMode.SuggestedCommands);
        Assert.DoesNotContain(" apply ", $" {planCommand} ", StringComparison.Ordinal);
        Assert.Contains("plan", planCommand, StringComparison.Ordinal);
        Assert.Contains("azure-gp-apply-mode:plan-only", planMode.ValidationChecks);

        // Only a gated execute pass renders the real apply.
        RuntimeAdapterStepPlan executeMode = adapter.ApplyInfrastructure(PlanRequest(dryRun: false));
        string applyCommand = Assert.Single(executeMode.SuggestedCommands);
        Assert.Contains(" apply ", $" {applyCommand} ", StringComparison.Ordinal);
        Assert.Contains("azure-gp-apply-mode:write-enabled", executeMode.ValidationChecks);
    }

    [Fact]
    public void Verify_BindsToSubstrateOutputs_AndControlPlaneBackend()
    {
        AzureGpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan verify = adapter.Verify(PlanRequest());

        Assert.Contains(verify.SuggestedCommands, c => c.Contains("output -raw gp_batch_account_url", StringComparison.Ordinal));
        Assert.Contains(verify.SuggestedCommands, c => c.Contains("output -raw gp_pool_id", StringComparison.Ordinal));
        Assert.Contains(verify.ValidationChecks, c => c == "azure-gp-control-plane-backend:honua-azure-batch");
    }

    [Fact]
    public void Rollback_InPlanMode_IsTerraformPlan_NeverApply()
    {
        AzureGpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan rollback = adapter.Rollback(PlanRequest(dryRun: true));
        string command = Assert.Single(rollback.SuggestedCommands);
        Assert.DoesNotContain(" apply ", $" {command} ", StringComparison.Ordinal);
        Assert.Contains("plan", command, StringComparison.Ordinal);
    }

    // --- Runtime sizing hint (single-pool overrides): pure, NO terraform, NO tier selection ---

    [Fact]
    public void ToSizingHint_ProducesPerTaskOverrides_WithExactServerKeys()
    {
        AzureGpResourceProfile profile = new(
            TaskTimeoutMinutes: 120,
            MaxTaskRetryCount: 2,
            ContainerImage: "myacr.azurecr.io/worker-gdal:v2");

        AzureGpSizingHint hint = profile.ToSizingHint("honua-gp-pool");

        Assert.True(hint.IsValid);
        // Keys MUST be the exact keys the server reads (honua-server AzureBatchComputeBackend).
        Assert.Equal("120", hint.SubmitTaskOverrides["azure.batch.task_timeout_minutes"]);
        Assert.Equal("2", hint.SubmitTaskOverrides["azure.batch.max_task_retry_count"]);
        Assert.Equal("honua-gp-pool", hint.SubmitTaskOverrides["azure.batch.pool_id"]);
        Assert.Equal("myacr.azurecr.io/worker-gdal:v2", hint.SubmitTaskOverrides["azure.batch.container_image"]);
        // No tier token, no vCPU/memory override (single-pool, per-pool sizing).
        Assert.DoesNotContain("azure.batch.vcpus", hint.SubmitTaskOverrides.Keys);
    }

    [Fact]
    public void ToSizingHint_OmitsContainerImage_WhenBlank()
    {
        AzureGpSizingHint hint = AzureGpResourceProfile.Baseline.ToSizingHint("honua-gp-pool");

        Assert.DoesNotContain("azure.batch.container_image", hint.SubmitTaskOverrides.Keys);
    }

    [Fact]
    public void ToSizingHint_OutOfBoundsTimeoutAndRetry_AreRejected()
    {
        Assert.False((AzureGpResourceProfile.Baseline with { TaskTimeoutMinutes = 0 }).ToSizingHint("p").IsValid);
        Assert.False((AzureGpResourceProfile.Baseline with { MaxTaskRetryCount = -1 }).ToSizingHint("p").IsValid);
        Assert.False((AzureGpResourceProfile.Baseline with { MaxTaskRetryCount = 101 }).ToSizingHint("p").IsValid);
        // 0 retries is valid on Azure (means no retry).
        Assert.True((AzureGpResourceProfile.Baseline with { MaxTaskRetryCount = 0 }).ToSizingHint("p").IsValid);
    }
}
