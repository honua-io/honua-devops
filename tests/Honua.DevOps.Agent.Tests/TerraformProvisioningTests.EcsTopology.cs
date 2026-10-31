using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Theory]
    [InlineData("{\"redis_enabled\":false,\"domain_name\":\"gis.example.com\",\"route53_zone_id\":\"Z123ABC\",\"subject_alternative_names\":[\"maps.example.com\"]}")]
    [InlineData("{\"redis_enabled\":false,\"region\":\"us-east-1\",\"domain_name\":\"gis.example.com\",\"alb_certificate_arn\":\"arn:aws:acm:us-east-1:123456789012:certificate/11111111-2222-3333-4444-555555555555\",\"domain_alias_record_enabled\":false,\"alb_enable_http_redirect\":true}")]
    public async Task EcsTopology_MapsValidatedInputsToTheSavedPlan(string variables)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway,
            provisioningProcessRunner: runner);
        OperationResponse result = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", variables, false, "");
        Assert.Equal("terraform-plan-ready", result.Status);
        ProcessCall plan = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        using JsonDocument actual = JsonDocument.Parse(File.ReadAllText(plan.Option("--var-file")!));
        using JsonDocument expected = JsonDocument.Parse(variables);
        foreach (JsonProperty input in expected.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(input.Value, actual.RootElement.GetProperty(input.Name)), input.Name);
        Assert.False(actual.RootElement.GetProperty("redis_enabled").GetBoolean());
        Assert.Equal(1, actual.RootElement.GetProperty("desired_count").GetInt32());
        Assert.False(actual.RootElement.GetProperty("canary_enabled").GetBoolean());
        Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
        DeleteSavedPlanFrom(plan);
    }

    [Theory]
    [InlineData("{\"redis_enabled\":\"false\"}")]
    [InlineData("{\"domain_name\":\"https://gis.example.com\",\"route53_zone_id\":\"Z123ABC\"}")]
    [InlineData("{\"domain_name\":\"*.example.com\",\"route53_zone_id\":\"Z123ABC\"}")]
    [InlineData("{\"domain_name\":\"gis.example.com\"}")]
    [InlineData("{\"route53_zone_id\":\"Z123ABC\"}")]
    [InlineData("{\"domain_name\":\"gis.example.com\",\"route53_zone_id\":\"../zone\"}")]
    [InlineData("{\"alb_certificate_arn\":\"arn:aws:iam::123456789012:user/admin\"}")]
    [InlineData("{\"region\":\"us-west-2\",\"alb_certificate_arn\":\"arn:aws:acm:us-east-1:123456789012:certificate/11111111-2222-3333-4444-555555555555\"}")]
    [InlineData("{\"subject_alternative_names\":[\"maps.example.com\"]}")]
    [InlineData("{\"desired_count\":2}")]
    [InlineData("{\"max_capacity\":2}")]
    [InlineData("{\"canary_enabled\":true}")]
    [InlineData("{\"db_publicly_accessible\":true}")]
    [InlineData("{\"additional_env\":{}}")]
    [InlineData("{\"db_password\":\"secret\"}")]
    public async Task EcsTopology_InvalidInputsStartNoProcess(string variables)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway,
            provisioningProcessRunner: runner);
        OperationResponse result = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", variables, false, "");
        Assert.Equal("variables-invalid", result.Status);
        Assert.Empty(runner.Calls);
    }
}
