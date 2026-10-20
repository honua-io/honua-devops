using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    private static Dictionary<string, object?> LambdaMigrationVariables() => new()
    {
        ["region"] = "us-west-2",
        ["honua_image_uri"] = "registry.example/honua@sha256:" + new string('a', 64),
        ["migration_image"] = "registry.example/honua@sha256:" + new string('b', 64),
        ["migration_packaged_manifest_sha256"] = new string('c', 64),
        ["migration_target_locator_sha256"] = new string('d', 64)
    };

    [Fact]
    public async Task LambdaMigrationProfileReachesOnlyTheExactReviewedPlanAsNonSecretPins()
    {
        using var root = new TerraformTestRoot(rootName: "aws-serverless");
        var runner = new FakeSubstrateRunner();
        using var gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var toolkit = new HonuaOperationsToolkit(LambdaRuntime(root.Path), gateway, provisioningProcessRunner: runner);
        var variables = LambdaMigrationVariables();
        var response = await toolkit.ProvisionInfrastructureAsync("aws-lambda", "small", "plan",
            JsonSerializer.Serialize(variables), false, "");

        Assert.Equal("terraform-plan-ready", response.Status);
        var plan = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        using var actual = JsonDocument.Parse(await File.ReadAllTextAsync(plan.Option("--var-file")!));
        foreach (var key in variables.Keys)
            Assert.Equal((string)variables[key]!, actual.RootElement.GetProperty(key).GetString());
        Assert.True(actual.RootElement.GetProperty("skip_migrations").GetBoolean());
        Assert.Equal(root.AwsRoot, plan.Option("--root"));
        Assert.DoesNotContain(runner.Calls, call => call.Operation is "init" or "plan" or "apply");
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("missing-image")]
    [InlineData("tagged-image")]
    [InlineData("foreign-repository")]
    [InlineData("invalid-manifest")]
    [InlineData("uppercase-manifest")]
    [InlineData("invalid-locator")]
    [InlineData("orphan-locator")]
    [InlineData("null-image")]
    [InlineData("environment-override")]
    [InlineData("connection-override")]
    public async Task LambdaMigrationInvalidOrSecretInputsNeverStartAProviderProcess(string scenario)
    {
        var variables = LambdaMigrationVariables();
        switch (scenario)
        {
            case "missing-manifest": variables.Remove("migration_packaged_manifest_sha256"); break;
            case "missing-image": variables.Remove("migration_image"); break;
            case "tagged-image": variables["migration_image"] = "registry.example/honua:v1"; break;
            case "foreign-repository": variables["migration_image"] = "registry.example/other@sha256:" + new string('b', 64); break;
            case "invalid-manifest": variables["migration_packaged_manifest_sha256"] = "short"; break;
            case "uppercase-manifest": variables["migration_packaged_manifest_sha256"] = new string('C', 64); break;
            case "invalid-locator": variables["migration_target_locator_sha256"] = "short"; break;
            case "orphan-locator": variables.Remove("migration_image"); variables.Remove("migration_packaged_manifest_sha256"); break;
            case "null-image": variables["migration_image"] = null; break;
            case "environment-override": variables["additional_env"] = new { Password = "synthetic-secret-must-not-echo" }; break;
            case "connection-override": variables["ConnectionStrings__DefaultConnection"] = "synthetic-secret-must-not-echo"; break;
        }
        using var root = new TerraformTestRoot(rootName: "aws-serverless");
        var runner = new FakeSubstrateRunner();
        using var gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var toolkit = new HonuaOperationsToolkit(LambdaRuntime(root.Path), gateway, provisioningProcessRunner: runner);
        var response = await toolkit.ProvisionInfrastructureAsync("aws-lambda", "small", "plan",
            JsonSerializer.Serialize(variables), false, "");

        Assert.Equal("variables-invalid", response.Status);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain("synthetic-secret-must-not-echo", response.Summary);
    }

    [Theory]
    [InlineData("migration_image")]
    [InlineData("migration_packaged_manifest_sha256")]
    [InlineData("migration_target_locator_sha256")]
    public async Task LambdaMigrationPinsCannotChangeAfterIndependentPlanApproval(string changedKey)
    {
        using var root = new TerraformTestRoot(rootName: "aws-serverless");
        var runner = new FakeSubstrateRunner();
        using var gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var runtime = LambdaRuntime(root.Path);
        var planner = new HonuaOperationsToolkit(runtime, gateway, provisioningProcessRunner: runner);
        var variables = LambdaMigrationVariables();
        var plan = await planner.ProvisionInfrastructureAsync("aws-lambda", "small", "plan",
            JsonSerializer.Serialize(variables), false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        var challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        var approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply", stack: "aws-lambda");
        variables[changedKey] = changedKey == "migration_image"
            ? "registry.example/honua@sha256:" + new string('e', 64)
            : new string('e', 64);
        var callsBefore = runner.Calls.Count;
        var executor = new HonuaOperationsToolkit(runtime with
        {
            ExecutionMode = ExecutionMode.Execute, ExecutionTier = ExecutionTier.ExecuteLowerEnv
        }, gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        var response = await executor.ProvisionInfrastructureAsync("aws-lambda", "small", "apply",
            JsonSerializer.Serialize(variables), true, challenge, approval);

        Assert.Equal("idempotency-conflict", response.Status);
        Assert.Equal(callsBefore, runner.Calls.Count);
        Assert.Equal(0, runner.ApplyCalls);
    }
}
