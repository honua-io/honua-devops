using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class ProvisionedRuntimeFactsTests
{
    [Theory]
    [InlineData("aws-ecs", "ecs", "aws", "AwsEcs", "honua-aws-ecs-alb")]
    [InlineData("aws-lambda", "lambda", "aws-serverless", "AwsLambda", "honua-gitops-aws-lambda")]
    [InlineData("aws-eks", "eks", "aws-eks", "Kubernetes", "honua-kubernetes-deployment")]
    public void HostRuntimeSelectsExactKnownRootAndActualBackend(string kind, string target, string root, string targetKind, string backend)
    {
        var profile = ProvisioningRuntimeProfile.Resolve(kind);
        Assert.Equal(target, profile.TerraformTarget);
        Assert.Equal(root, profile.TerraformRootName);
        Assert.Equal(targetKind, profile.TargetKind);
        Assert.Equal(backend, profile.Backend);
        Assert.Throws<InvalidOperationException>(() => ProvisioningRuntimeProfile.Resolve("../../aws"));
        Assert.Equal("aws-ecs", ProvisioningRuntimeProfile.Resolve(null).RuntimeKind);
    }

    [Fact]
    public void LambdaFactsBindExactAccountRegionFunctionAliasAndRegisteredScope()
    {
        var projection = LambdaProjection();
        Assert.True(Read(projection, out var facts));
        Assert.Equal("aws-lambda", facts!.RuntimeKind);
        Assert.Equal("42", facts.ResourceIdentity.GetProperty("publishedVersion").GetString());
        projection["resourceIdentity"]!["aliasArn"] = "arn:aws:lambda:us-west-2:123456789012:function:another:live";
        Assert.False(Read(projection, out _));
        projection = LambdaProjection();
        projection["platform"]!["accountId"] = "999999999999";
        Assert.False(Read(projection, out _));
        projection = LambdaProjection();
        projection["platform"]!["region"] = "us-east-1";
        Assert.False(Read(projection, out _));
        projection = LambdaProjection();
        projection["targetId"] = "another-target";
        Assert.False(Read(projection, out _));
        projection = LambdaProjection();
        projection["environment"] = "prod";
        Assert.False(Read(projection, out _));
    }

    [Fact]
    public void FactsCannotRelabelDifferentRuntimeOrClaimObservationOrCarryCredentials()
    {
        var projection = LambdaProjection();
        projection["backend"] = "honua-gitops-kubernetes";
        Assert.False(Read(projection, out _));
        projection = LambdaProjection();
        projection["resourceIdentity"]!["publishedVersion"] = "$LATEST";
        Assert.False(Read(projection, out _));
        foreach (var suffix in new[] { "$LATEST", "42", "bad:alias", "with space" })
        {
            projection = LambdaProjection();
            projection["resourceIdentity"]!["aliasArn"] = "arn:aws:lambda:us-west-2:123456789012:function:honua-dev:" + suffix;
            projection["resourceIdentity"]!["apiGatewayIntegrationAliasArn"] = projection["resourceIdentity"]!["aliasArn"]!.DeepClone();
            Assert.False(Read(projection, out _));
        }
        projection = LambdaProjection();
        projection["observation"] = new JsonObject { ["verified"] = true };
        Assert.False(Read(projection, out _));
        projection = LambdaProjection();
        projection["credentials"] = "must-never-be-accepted";
        Assert.False(Read(projection, out _));
        Assert.False(ProvisionedRuntimeFacts.TryReadOutputs("{}", ProvisioningRuntimeProfile.Resolve("aws-lambda"),
            "lambda-dev", "dev", out _, out _));
    }

    private static bool Read(JsonNode projection, out ProvisionedRuntimeFacts? facts) =>
        ProvisionedRuntimeFacts.TryReadOutputs(new JsonObject
        { [ProvisionedRuntimeFacts.OutputName] = new JsonObject { ["value"] = projection.DeepClone() } }.ToJsonString(),
            ProvisioningRuntimeProfile.Resolve("aws-lambda"), "lambda-dev", "dev", out facts, out _);

    private static JsonNode LambdaProjection() => JsonNode.Parse("""
        {"schemaVersion":"honua.runtime-binding/v1","runtimeKind":"aws-lambda","targetKind":"AwsLambda",
         "backend":"honua-gitops-aws-lambda","targetId":"lambda-dev","environment":"dev",
         "platform":{"accountId":"123456789012","region":"us-west-2","partition":"aws"},
         "resourceIdentity":{"functionArn":"arn:aws:lambda:us-west-2:123456789012:function:honua-dev",
           "aliasArn":"arn:aws:lambda:us-west-2:123456789012:function:honua-dev:live","publishedVersion":"42",
           "apiGatewayId":"abc123","apiGatewayIntegrationId":"def456","apiGatewayStage":"$default",
           "apiGatewayRouteId":"route123","apiGatewayRouteKey":"ANY /{proxy+}",
           "apiGatewayIntegrationAliasArn":"arn:aws:lambda:us-west-2:123456789012:function:honua-dev:live"}}
        """)!;
}
