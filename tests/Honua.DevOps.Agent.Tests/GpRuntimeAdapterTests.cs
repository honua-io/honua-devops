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
        ChangeSummary: "gp per-job batch provision",
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

    [Fact]
    public void Mapping_FargateBaseline_MapsToExactGpBatchVariableNames()
    {
        GpResourceProfile profile = GpResourceProfile.FargateBaseline;

        IReadOnlyList<GpBatchTerraformVar> vars = GpBatchTerraformMapping.ToTerraformVars(profile);
        Dictionary<string, string> byName = vars.ToDictionary(v => v.Name, v => v.Value);

        // Exact honua-iac modules/aws-serverless variable names (PR #70). The gate is always on.
        Assert.Equal("true", byName["enable_gp_batch"]);
        Assert.Equal(string.Empty, byName["gp_batch_image"]);
        Assert.Equal("1", byName["gp_batch_vcpus"]);
        Assert.Equal("2048", byName["gp_batch_memory_mib"]);
        Assert.Equal("X86_64", byName["gp_batch_cpu_architecture"]);
        Assert.Equal("0", byName["gp_batch_gpu_count"]);
        // Unset ephemeral storage renders as null (Fargate 20 GiB default), matching the iac default.
        Assert.Equal("null", byName["gp_batch_ephemeral_storage_gib"]);
        Assert.Equal("3600", byName["gp_batch_timeout_seconds"]);
        Assert.Equal("1", byName["gp_batch_retry_attempts"]);
        Assert.Equal("geoprocessing-batch", byName["gp_batch_workload_id"]);
        Assert.Equal("false", byName["create_worker_gdal_repo"]);
    }

    [Fact]
    public void Mapping_HighMemoryArmProfile_RendersArchAndEphemeralStorage()
    {
        GpResourceProfile profile = new(
            Vcpus: 8,
            MemoryMib: 61440,
            GpuCount: 0,
            TimeoutSeconds: 14400,
            Architecture: GpCpuArchitecture.Arm64,
            Image: "123456789012.dkr.ecr.us-east-1.amazonaws.com/worker-gdal:job-abc",
            EphemeralStorageGib: 150,
            RetryAttempts: 3,
            CreateWorkerGdalRepo: true,
            WorkloadId: "gp-bigmem");

        Dictionary<string, string> byName = GpBatchTerraformMapping
            .ToTerraformVars(profile)
            .ToDictionary(v => v.Name, v => v.Value);

        Assert.Equal("8", byName["gp_batch_vcpus"]);
        Assert.Equal("61440", byName["gp_batch_memory_mib"]);
        Assert.Equal("ARM64", byName["gp_batch_cpu_architecture"]);
        Assert.Equal("150", byName["gp_batch_ephemeral_storage_gib"]);
        Assert.Equal("14400", byName["gp_batch_timeout_seconds"]);
        Assert.Equal("3", byName["gp_batch_retry_attempts"]);
        Assert.Equal("gp-bigmem", byName["gp_batch_workload_id"]);
        Assert.Equal("true", byName["create_worker_gdal_repo"]);
        Assert.Equal("123456789012.dkr.ecr.us-east-1.amazonaws.com/worker-gdal:job-abc", byName["gp_batch_image"]);

        // The profile is valid on Fargate-Spot (no GPU).
        Assert.True(profile.Validate(GpResourceProfile.ComputePlatform.FargateSpot).IsValid);
    }

    [Fact]
    public void Validate_GpuOnFargateSpot_IsRejectedWithEc2Guidance()
    {
        GpResourceProfile gpuProfile = GpResourceProfile.FargateBaseline with { GpuCount = 2 };

        GpProfileValidation fargate = gpuProfile.Validate(GpResourceProfile.ComputePlatform.FargateSpot);
        Assert.False(fargate.IsValid);
        Assert.Contains(fargate.Errors, e => e.Contains("Fargate", StringComparison.Ordinal) && e.Contains("EC2", StringComparison.Ordinal));

        // The same GPU profile IS valid on an EC2 compute environment.
        GpProfileValidation ec2 = gpuProfile.Validate(GpResourceProfile.ComputePlatform.Ec2);
        Assert.True(ec2.IsValid);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeEphemeralStorageAndRetryAndGpu()
    {
        Assert.False(
            (GpResourceProfile.FargateBaseline with { EphemeralStorageGib = 10 })
                .Validate(GpResourceProfile.ComputePlatform.FargateSpot).IsValid);
        Assert.False(
            (GpResourceProfile.FargateBaseline with { EphemeralStorageGib = 250 })
                .Validate(GpResourceProfile.ComputePlatform.FargateSpot).IsValid);
        Assert.False(
            (GpResourceProfile.FargateBaseline with { RetryAttempts = 11 })
                .Validate(GpResourceProfile.ComputePlatform.FargateSpot).IsValid);
        Assert.False(
            (GpResourceProfile.FargateBaseline with { GpuCount = 32 })
                .Validate(GpResourceProfile.ComputePlatform.Ec2).IsValid);
    }

    [Fact]
    public void PlanInfra_EmitsTerraformPlanWithGpBatchVarFlags()
    {
        GpRuntimeAdapter adapter = new(GpResourceProfile.FargateBaseline);

        RuntimeAdapterStepPlan plan = adapter.PlanInfrastructure(PlanRequest());

        string command = Assert.Single(plan.SuggestedCommands);
        Assert.Contains("terraform -chdir", command, StringComparison.Ordinal);
        Assert.Contains(" plan ", $" {command} ", StringComparison.Ordinal);
        Assert.Contains("examples/aws-cert", command, StringComparison.Ordinal);
        Assert.Contains("enable_gp_batch=true", command, StringComparison.Ordinal);
        Assert.Contains("gp_batch_vcpus=1", command, StringComparison.Ordinal);
        // Plan never apply.
        Assert.DoesNotContain(" apply ", $" {command} ", StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyInfra_InPlanMode_DegradesToTerraformPlan_NoApply()
    {
        GpRuntimeAdapter adapter = new(GpResourceProfile.FargateBaseline);

        RuntimeAdapterStepPlan planMode = adapter.ApplyInfrastructure(PlanRequest(dryRun: true));
        string planCommand = Assert.Single(planMode.SuggestedCommands);
        Assert.DoesNotContain("terraform -chdir /tmp/honua-iac/infrastructure/terraform/examples/aws-cert apply", planCommand, StringComparison.Ordinal);
        Assert.Contains("plan", planCommand, StringComparison.Ordinal);
        Assert.Contains("gp-apply-mode:plan-only", planMode.ValidationChecks);

        // Only a gated execute pass renders the real apply.
        RuntimeAdapterStepPlan executeMode = adapter.ApplyInfrastructure(PlanRequest(dryRun: false));
        string applyCommand = Assert.Single(executeMode.SuggestedCommands);
        Assert.Contains(" apply ", $" {applyCommand} ", StringComparison.Ordinal);
        Assert.Contains("gp-apply-mode:write-enabled", executeMode.ValidationChecks);
    }

    [Fact]
    public void Validate_InvalidProfile_SurfacesRejectionWithoutCommandsToApply()
    {
        GpRuntimeAdapter adapter = new(
            GpResourceProfile.FargateBaseline with { GpuCount = 4 },
            GpResourceProfile.ComputePlatform.FargateSpot);

        RuntimeAdapterStepPlan plan = adapter.Validate(PlanRequest());

        Assert.Contains("gp-profile-valid:false", plan.ValidationChecks);
        Assert.Contains(plan.Risks, r => r.Contains("GP profile rejected", StringComparison.Ordinal));
    }
}
