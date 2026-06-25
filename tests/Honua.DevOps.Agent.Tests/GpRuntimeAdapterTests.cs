using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Tests;

public sealed class GpRuntimeAdapterTests
{
    private static RuntimeAdapterRequest PlanRequest(bool dryRun = true) => new(
        Service: "geoprocessing",
        Environments: ["dev"],
        Revision: "HEAD",
        Action: "apply",
        ChangeSummary: "gp per-env substrate provision",
        GitOpsTool: "honua-gitops",
        TerraformRepository: "https://github.com/honua-io/honua-iac",
        TerraformRef: "trunk",
        TerraformLocalPath: "/tmp/honua-iac",
        DryRun: dryRun,
        ExecutionMode: dryRun ? ExecutionMode.Plan : ExecutionMode.Execute,
        ExecutionTier: dryRun ? ExecutionTier.Plan : ExecutionTier.ExecuteLowerEnv);

    [Fact]
    public void Registry_ResolvesGpTarget_AsBatchJobFamily()
    {
        RuntimeAdapterCapability capability = RuntimeAdapterCatalog.Resolve("gp");

        Assert.Equal("gp", capability.Target);
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
    public void Registry_ResolvesGpAlongsideExistingSixTargets()
    {
        IReadOnlyList<RuntimeAdapterCapability> all = RuntimeAdapterCatalog.ResolveMany(
            ["azure-functions", "lambda", "aks", "eks", "ecs", "aca", "gp"]);

        Assert.Equal(7, all.Count);
        Assert.Contains(all, c => c.Target == "gp");
    }

    // --- Substrate provisioning (per-ENV, GitOps-gated): NO per-job profile inputs ---

    [Fact]
    public void Substrate_Default_RendersPerEnvVars_NoPerJobKnobs()
    {
        IReadOnlyList<GpSubstrateVar> vars = GpSubstrateConfig.Default.ToSubstrateVars();
        Dictionary<string, string> byName = vars.ToDictionary(v => v.Name, v => v.Value);

        // Per-env substrate inputs only. The gate is always on.
        Assert.Equal("true", byName["enable_gp_substrate"]);
        Assert.Equal(string.Empty, byName["gp_worker_image"]);
        Assert.Equal("X86_64", byName["gp_cpu_architecture"]);
        Assert.Equal("256", byName["gp_compute_max_vcpus"]);
        // The full pooled tier set.
        Assert.Equal("s,m,l,xl", byName["gp_job_definition_tiers"]);
        Assert.Equal("geoprocessing-batch", byName["gp_workload_id"]);
        Assert.Equal("true", byName["create_worker_gdal_repo"]);

        // Per-job knobs are NOT terraform inputs — they are SubmitJob overrides.
        Assert.DoesNotContain("gp_batch_vcpus", byName.Keys);
        Assert.DoesNotContain("gp_batch_memory_mib", byName.Keys);
        Assert.DoesNotContain("gp_batch_timeout_seconds", byName.Keys);
        Assert.DoesNotContain("gp_batch_retry_attempts", byName.Keys);
        Assert.DoesNotContain("gp_batch_gpu_count", byName.Keys);
    }

    [Fact]
    public void Substrate_ArmSubsetTiers_RendersArchAndTierPool()
    {
        GpSubstrateConfig substrate = new(
            Image: "123456789012.dkr.ecr.us-east-1.amazonaws.com/worker-gdal:latest",
            Architecture: GpCpuArchitecture.Arm64,
            MaxVcpus: 512,
            CreateWorkerGdalRepo: false,
            WorkloadId: "gp-bigmem",
            Tiers: [GpJobDefinitionTier.M, GpJobDefinitionTier.L, GpJobDefinitionTier.Xl]);

        Dictionary<string, string> byName = substrate.ToSubstrateVars().ToDictionary(v => v.Name, v => v.Value);

        Assert.Equal("ARM64", byName["gp_cpu_architecture"]);
        Assert.Equal("512", byName["gp_compute_max_vcpus"]);
        Assert.Equal("m,l,xl", byName["gp_job_definition_tiers"]);
        Assert.Equal("gp-bigmem", byName["gp_workload_id"]);
        Assert.Equal("false", byName["create_worker_gdal_repo"]);
        Assert.Equal("123456789012.dkr.ecr.us-east-1.amazonaws.com/worker-gdal:latest", byName["gp_worker_image"]);
    }

    [Fact]
    public void PlanInfra_EmitsTerraformPlanWithSubstrateVarFlags_AndBindsToOutputs()
    {
        GpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan plan = adapter.PlanInfrastructure(PlanRequest());

        string command = Assert.Single(plan.SuggestedCommands);
        Assert.Contains("terraform -chdir", command, StringComparison.Ordinal);
        Assert.Contains(" plan ", $" {command} ", StringComparison.Ordinal);
        Assert.Contains("enable_gp_substrate=true", command, StringComparison.Ordinal);
        Assert.Contains("gp_job_definition_tiers=s,m,l,xl", command, StringComparison.Ordinal);
        // Plan never apply.
        Assert.DoesNotContain(" apply ", $" {command} ", StringComparison.Ordinal);

        // No per-job profile var leaked into the plan.
        Assert.DoesNotContain("gp_batch_vcpus", command, StringComparison.Ordinal);

        // The plan announces it binds to the substrate OUTPUTS (ARNs), not input-variable names.
        Assert.Contains(plan.ValidationChecks, c => c.Contains("gp_job_queue_arn", StringComparison.Ordinal));
        Assert.Contains(plan.ValidationChecks, c => c.Contains("gp_job_definition_arns", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyInfra_InPlanMode_DegradesToTerraformPlan_NoApply()
    {
        GpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan planMode = adapter.ApplyInfrastructure(PlanRequest(dryRun: true));
        string planCommand = Assert.Single(planMode.SuggestedCommands);
        Assert.DoesNotContain("examples/aws-cert apply", planCommand, StringComparison.Ordinal);
        Assert.Contains("plan", planCommand, StringComparison.Ordinal);
        Assert.Contains("gp-apply-mode:plan-only", planMode.ValidationChecks);

        // Only a gated execute pass renders the real apply.
        RuntimeAdapterStepPlan executeMode = adapter.ApplyInfrastructure(PlanRequest(dryRun: false));
        string applyCommand = Assert.Single(executeMode.SuggestedCommands);
        Assert.Contains(" apply ", $" {applyCommand} ", StringComparison.Ordinal);
        Assert.Contains("gp-apply-mode:write-enabled", executeMode.ValidationChecks);
    }

    [Fact]
    public void Verify_BindsToSubstrateOutputArns_NotInputVariableNames()
    {
        GpRuntimeAdapter adapter = new();

        RuntimeAdapterStepPlan verify = adapter.Verify(PlanRequest());

        Assert.Contains(verify.SuggestedCommands, c => c.Contains("output -raw gp_job_queue_arn", StringComparison.Ordinal));
        Assert.Contains(verify.SuggestedCommands, c => c.Contains("output -json gp_job_definition_arns", StringComparison.Ordinal));
        Assert.Contains(verify.ValidationChecks, c => c == "gp-substrate-output:gp_compute_environment_arn");
    }

    // --- Runtime sizing hint (tier-select + SubmitJob overrides): pure, NO terraform ---

    [Theory]
    [InlineData(null, "s")]
    [InlineData(0, "s")]
    [InlineData(1, "s")]
    [InlineData(20, "s")]
    [InlineData(21, "m")]
    [InlineData(50, "m")]
    [InlineData(51, "l")]
    [InlineData(100, "l")]
    [InlineData(101, "xl")]
    [InlineData(200, "xl")]
    public void SelectTier_BoundaryCases_PickCorrectTier(int? ephemeralGib, string expectedToken)
    {
        GpJobDefinitionTier? tier = GpResourceProfile.SelectTier(ephemeralGib);

        Assert.NotNull(tier);
        Assert.Equal(expectedToken, GpResourceProfile.TierToken(tier!.Value));
    }

    [Fact]
    public void SelectTier_AboveFargateCeiling_ReturnsNull()
    {
        Assert.Null(GpResourceProfile.SelectTier(201));
        Assert.Null(GpResourceProfile.SelectTier(500));
    }

    [Fact]
    public void ToSizingHint_ProducesSubmitJobOverrides_AndTierArnPath()
    {
        GpResourceProfile profile = new(
            Vcpus: 4,
            MemoryMib: 16384,
            TimeoutSeconds: 7200,
            RetryAttempts: 2,
            EphemeralStorageGib: 75);

        GpSizingHint hint = profile.ToSizingHint();

        Assert.True(hint.IsValid);
        Assert.Equal("l", hint.TierToken);
        Assert.Equal("4", hint.SubmitJobOverrides["batch.vcpus"]);
        Assert.Equal("16384", hint.SubmitJobOverrides["batch.memoryMib"]);
        Assert.Equal("7200", hint.SubmitJobOverrides["batch.timeoutSeconds"]);
        Assert.Equal("2", hint.SubmitJobOverrides["batch.retryAttempts"]);
        // Ephemeral storage is the TIER, not a SubmitJob override.
        Assert.DoesNotContain("batch.ephemeralStorageGib", hint.SubmitJobOverrides.Keys);
        Assert.Equal("gp_job_definition_arns.l", GpSubstrateOutputs.JobDefinitionArnForTier(hint.Tier!.Value));
    }

    [Fact]
    public void ToSizingHint_AboveCeiling_IsInvalidWithError_NoTier()
    {
        GpSizingHint hint = (GpResourceProfile.Baseline with { EphemeralStorageGib = 250 }).ToSizingHint();

        Assert.False(hint.IsValid);
        Assert.Null(hint.Tier);
        Assert.Contains(hint.Errors, e => e.Contains("200", StringComparison.Ordinal) && e.Contains("ceiling", StringComparison.Ordinal));
    }

    [Fact]
    public void ToSizingHint_Gpu_IsAdvisoryNote_NotHardReject()
    {
        // GPU is unsupported on the default Fargate substrate, but it is a NOTE, not an error:
        // the hint is still valid and steers the operator to an opt-in GPU compute-env.
        GpSizingHint hint = (GpResourceProfile.Baseline with { GpuCount = 2 }).ToSizingHint();

        Assert.True(hint.IsValid);
        Assert.Contains(hint.Notes, n => n.Contains("GPU", StringComparison.OrdinalIgnoreCase) && n.Contains("Fargate", StringComparison.Ordinal));
        // GPU is never emitted as a Fargate SubmitJob override.
        Assert.DoesNotContain("batch.gpuCount", hint.SubmitJobOverrides.Keys);
    }

    [Fact]
    public void ToSizingHint_OutOfBoundsRetryAndVcpus_AreRejected()
    {
        Assert.False((GpResourceProfile.Baseline with { RetryAttempts = 11 }).ToSizingHint().IsValid);
        Assert.False((GpResourceProfile.Baseline with { Vcpus = 0 }).ToSizingHint().IsValid);
        Assert.False((GpResourceProfile.Baseline with { MemoryMib = 0 }).ToSizingHint().IsValid);
        Assert.False((GpResourceProfile.Baseline with { TimeoutSeconds = 0 }).ToSizingHint().IsValid);
    }
}
