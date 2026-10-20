using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class RegisteredRuntimeAuthorityTests
{
    private const string Function = "arn:aws:lambda:us-west-2:123456789012:function:honua";
    private static OperationRuntime Runtime() => ProvisioningSubstrateFixtures.CreateRuntime("/host/iac", ExecutionMode.Plan, ExecutionTier.Plan) with
    {
        RuntimeKind = "aws-lambda", RuntimeBackend = "honua-gitops-aws-lambda", ServerTargetId = "server-local", DeployTargetId = "honua-live"
    };
    private static ProvisionedRuntimeFacts Facts() => new("aws-lambda", "AwsLambda", "honua-gitops-aws-lambda",
        "honua-live", "dev", "123456789012", "us-west-2", "aws", JsonSerializer.SerializeToElement(new
        { functionArn = Function, aliasArn = Function + ":live" }));
    private static JsonObject Response() => new()
    {
        ["targetId"] = "server-local", ["deployTargetId"] = "honua-live", ["environment"] = "dev",
        ["runtimeKind"] = "aws-lambda", ["backend"] = "honua-gitops-aws-lambda", ["tenantId"] = null,
        ["targetName"] = Function, ["usesCustomProviderEndpoint"] = false,
        ["resourceParameters"] = new JsonObject { ["aws.region"] = "us-west-2" }
    };
    private static RegisteredRuntimeAuthority Read(JsonObject response) => RegisteredRuntimeAuthority.ReadLambda(
        JsonSerializer.SerializeToElement(response), Runtime(), Facts());

    [Fact]
    public void ExplicitUnboundTenantIsPreservedWithoutInventingPublic()
    {
        Assert.Null(Read(Response()).TenantId);
        var response = Response(); response["tenantId"] = "tenant-a";
        Assert.Equal("tenant-a", Read(response).TenantId);
    }

    [Theory]
    [InlineData("missing-tenant")]
    [InlineData("empty-tenant")]
    [InlineData("missing-endpoint-flag")]
    [InlineData("custom-endpoint")]
    [InlineData("short-effective-function")]
    [InlineData("conflicting-shadowed-arn")]
    [InlineData("conflicting-shadowed-region")]
    [InlineData("wrong-alias")]
    [InlineData("wrong-server")]
    [InlineData("secret-parameter")]
    public void MissingOrConflictingCurrentRegistryProofIsRefused(string scenario)
    {
        var response = Response(); var parameters = (JsonObject)response["resourceParameters"]!;
        switch (scenario)
        {
            case "missing-tenant": response.Remove("tenantId"); break;
            case "empty-tenant": response["tenantId"] = " "; break;
            case "missing-endpoint-flag": response.Remove("usesCustomProviderEndpoint"); break;
            case "custom-endpoint": response["usesCustomProviderEndpoint"] = true; break;
            case "short-effective-function": parameters["aws.lambda.function_name"] = "honua"; parameters["lambda.function_arn"] = Function; break;
            case "conflicting-shadowed-arn": parameters["lambda.function_arn"] = Function.Replace("123456789012", "999999999999"); break;
            case "conflicting-shadowed-region": parameters["lambda.region"] = "us-east-1"; break;
            case "wrong-alias": parameters["lambda.alias"] = "other"; break;
            case "wrong-server": response["targetId"] = "other"; break;
            case "secret-parameter": parameters["lambda.service_url"] = "https://redirect.invalid"; break;
        }
        var error = Assert.Throws<InvalidDataException>(() => Read(response));
        Assert.DoesNotContain("redirect.invalid", error.Message);
    }

    [Fact]
    public void RegionCanOnlyComeFromActualBackendArnMetadataPrecedence()
    {
        var response = Response(); var parameters = (JsonObject)response["resourceParameters"]!;
        parameters.Remove("aws.region");
        Assert.Throws<InvalidDataException>(() => Read(response)); // Backend does not derive region from TargetName.
        parameters["lambda.alias_arn"] = Function + ":live";
        Assert.Equal(Function, Read(response).TargetName);
    }
}
