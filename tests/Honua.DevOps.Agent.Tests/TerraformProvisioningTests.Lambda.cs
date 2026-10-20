using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    private static OperationRuntime LambdaRuntime(string root) => ProvisioningSubstrateFixtures.CreateRuntime(root, ExecutionMode.Plan, ExecutionTier.Plan) with
    {
        RuntimeKind = "aws-lambda", RuntimeBackend = "honua-gitops-aws-lambda", ServerTargetId = "local",
        DeployTargetId = "honua-dev-dev-honua-live", TerraformDeploymentTargets = ["lambda"]
    };

    [Fact]
    public async Task LambdaPlanUsesSameExactPlanWrapperAndActualServerlessRoot()
    {
        using var root = new TerraformTestRoot(rootName: "aws-serverless");
        var runner = new FakeSubstrateRunner();
        using var gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var toolkit = new HonuaOperationsToolkit(LambdaRuntime(root.Path), gateway, provisioningProcessRunner: runner);
        var response = await toolkit.ProvisionInfrastructureAsync("aws-lambda", "small", "plan",
            "{\"region\":\"us-west-2\",\"honua_image_uri\":\"registry.example/honua@sha256:" + new string('a', 64) + "\"}",
            confirmed: false, confirmation: "");
        Assert.Equal("terraform-plan-ready", response.Status);
        var plan = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        Assert.Equal(root.AwsRoot, plan.Option("--root"));
        Assert.Equal("honua-dev-dev-honua-live", plan.Option("--target-id"));
        Assert.DoesNotContain(runner.Calls, call => call.Operation is "init" or "plan" or "apply");
    }

    [Theory]
    [InlineData("{\"region\":\"us-west-2\",\"honua_image_uri\":\"registry.example/honua:latest\"}")]
    [InlineData("{\"region\":\"us-west-2\",\"honua_image_uri\":\"registry.example/honua:v1\"}")]
    [InlineData("{\"region\":\"us-west-2\",\"honua_admin_password\":\"secret-must-not-pass\"}")]
    [InlineData("{\"region\":\"us-west-2\",\"region\":\"us-east-1\"}")]
    public async Task LambdaUnreviewableVariablesNeverReachProvider(string variables)
    {
        using var root = new TerraformTestRoot(rootName: "aws-serverless");
        var runner = new FakeSubstrateRunner();
        using var gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var toolkit = new HonuaOperationsToolkit(LambdaRuntime(root.Path), gateway, provisioningProcessRunner: runner);
        var result = await toolkit.ProvisionInfrastructureAsync("aws-lambda", "small", "plan", variables, confirmed: false, confirmation: "");
        Assert.NotEqual("terraform-plan-ready", result.Status);
        Assert.Empty(runner.Calls);
    }
}
