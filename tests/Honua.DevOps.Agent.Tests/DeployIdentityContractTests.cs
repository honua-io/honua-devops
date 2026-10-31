using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class DeployIdentityContractTests
{
    [Fact]
    public async Task Plan_PresentsExactContractActionsAndBindsItsHashToTerraformInputs()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan);
        HonuaOperationsToolkit toolkit = new(runtime, gateway, provisioningProcessRunner: runner);
        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        var contract = DeployIdentityContract.Load(runtime.DeployIdentityContractPath);
        Assert.Equal(new ProvisioningTopology("ecs", true, false, false, false), plan.Topology);
        Assert.Contains(plan.Findings, finding => finding.Contains("ecs:CreateService") && finding.Contains("ecs:DescribeServices"));
        Assert.Contains(plan.Findings, finding => finding.Contains(plan.ApprovalRequest!.PlanMetadataDigest));
        var call = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        using var inputs = JsonDocument.Parse(File.ReadAllText(call.Option("--var-file")!));
        Assert.Equal(contract.Digest, inputs.RootElement.GetProperty("tags").GetProperty(DeployIdentityContract.DigestTag).GetString());
        Assert.Equal("", inputs.RootElement.GetProperty("waf_web_acl_arn").GetString());
        Assert.Equal("", inputs.RootElement.GetProperty("redis_connection_string").GetString());
        Assert.Equal(contract.Json, File.ReadAllText(Path.Combine(Path.GetDirectoryName(call.Option("--plan-out"))!, "deploy-identity-contract.json")));
    }

    [Theory]
    [InlineData("arn:aws:iam::123456789012:user/admin")]
    [InlineData("arn:aws:iam::123456789012:root")]
    [InlineData("arn:aws:sts::123456789012:assumed-role/Administrator/session")]
    [InlineData("arn:aws:sts::999999999999:assumed-role/honua-deploy-dev/session")]
    public async Task PlanAndApply_RefuseBroaderOrLongLivedCallerBeforeTerraformStarts(string caller)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv);
        HonuaOperationsToolkit toolkit = new(runtime, gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        runner.CallerArn = caller;
        int calls = runner.Calls.Count;
        OperationResponse apply = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true, plan.ConfirmationChallenge!, approval);
        Assert.Equal("deploy-identity-role-required", apply.Status);
        Assert.Equal(0, runner.ApplyCalls);
        Assert.All(runner.Calls.Skip(calls), call => Assert.Equal("aws", call.FileName));
        calls = runner.Calls.Count;
        OperationResponse secondPlan = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("deploy-identity-role-required", secondPlan.Status);
        Assert.All(runner.Calls.Skip(calls), call => Assert.Equal("aws", call.FileName));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("topology")]
    [InlineData("user")]
    [InlineData("access-key")]
    public async Task Plan_RefusesMissingInvalidOrDifferentTopologyContract(string failure)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan);
        string path = runtime.DeployIdentityContractPath!;
        var contract = JsonNode.Parse(File.ReadAllText(path))!;
        if (failure == "missing") File.Delete(path);
        else
        {
            if (failure == "topology") contract["topology"]!["redis"] = false;
            if (failure == "user") contract["role_arn"] = "arn:aws:iam::123456789012:user/admin";
            if (failure == "access-key") contract["actions_by_service"]!["iam"] = new JsonArray("iam:CreateAccessKey");
            File.WriteAllText(path, contract.ToJsonString());
        }
        HonuaOperationsToolkit toolkit = new(runtime, gateway, provisioningProcessRunner: runner);
        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal(failure == "topology" ? "deploy-identity-topology-mismatch" : "deploy-identity-contract-required", plan.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Apply_RefusesContractSubstitutionEvenWhenRoleAndTopologyStayTheSame()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv);
        HonuaOperationsToolkit toolkit = new(runtime, gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        var contract = JsonNode.Parse(File.ReadAllText(runtime.DeployIdentityContractPath!))!;
        contract["actions_by_service"]!["ecs"] = new JsonArray("ecs:DeleteService");
        File.WriteAllText(runtime.DeployIdentityContractPath!, contract.ToJsonString());
        OperationResponse result = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true, plan.ConfirmationChallenge!, approval);
        Assert.Equal("deploy-identity-contract-mismatch", result.Status);
        Assert.Equal(0, runner.ApplyCalls);
    }

    [Fact]
    public async Task RecommendationAndIntake_EmitTheRequestedTopologyTuple()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway);
        var intake = await toolkit.AnalyzeCustomerRequirementsAsync("City portal", "small", "", "", "aws", enableRedis: false, customDomain: true);
        var recommendation = await toolkit.RecommendDeploymentTopologyAsync("dev", false, false, true, "small", "low", enableRedis: false, customDomain: true);
        var expected = new ProvisioningTopology("ecs", false, true, false, false);
        Assert.Equal(expected, intake.Topology);
        Assert.Equal(expected, recommendation.Topology);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(recommendation));
        Assert.Equal(5, json.RootElement.GetProperty("topology").EnumerateObject().Count());
        Assert.True(json.RootElement.GetProperty("topology").GetProperty("custom_domain").GetBoolean());
    }
}
