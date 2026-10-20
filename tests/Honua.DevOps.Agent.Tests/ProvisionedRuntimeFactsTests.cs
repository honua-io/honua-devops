using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class ProvisionedRuntimeFactsTests
{
    [Theory]
    [InlineData("aws-ecs", "ecs", "aws", "AwsEcs", "honua-aws-ecs-alb")]
    [InlineData("aws-lambda", "lambda", "aws-serverless", "AwsLambda", "honua-gitops-aws-lambda")]
    [InlineData("aws-eks", "eks", "aws-eks-native", "Kubernetes", "honua-kubernetes-deployment")]
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

    [Fact]
    public void NativeEksConfiguredOutputReadsBackButCannotClaimAChangedLoadBalancerOrSecret()
    {
        var projection = EksProjection();
        Assert.True(ReadEks(projection, out var facts));
        Assert.Equal("aws-eks", facts!.RuntimeKind);
        Assert.Equal("honua-kubernetes-deployment", facts.Backend);
        Assert.Equal("arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/0123456789abcdef",
            facts.ResourceIdentity.GetProperty("loadBalancerArn").GetString());
        projection["resourceIdentity"]!["loadBalancerArn"] =
            "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/net/other/0123456789abcdef";
        Assert.False(ReadEks(projection, out _));
        projection = EksProjection();
        projection["resourceIdentity"]!["masterKeySecretArn"] = "arn:aws:secretsmanager:us-west-2:999999999999:secret:other";
        Assert.False(ReadEks(projection, out _));
        projection = EksProjection();
        projection["observation"] = new JsonObject { ["verified"] = true };
        Assert.False(ReadEks(projection, out _));
    }

    private static bool Read(JsonNode projection, out ProvisionedRuntimeFacts? facts) =>
        ProvisionedRuntimeFacts.TryReadOutputs(new JsonObject
        { [ProvisionedRuntimeFacts.OutputName] = new JsonObject { ["value"] = projection.DeepClone() } }.ToJsonString(),
            ProvisioningRuntimeProfile.Resolve("aws-lambda"), "lambda-dev", "dev", out facts, out _);

    private static bool ReadEks(JsonNode projection, out ProvisionedRuntimeFacts? facts) =>
        ProvisionedRuntimeFacts.TryReadOutputs(new JsonObject
        { [ProvisionedRuntimeFacts.OutputName] = new JsonObject { ["value"] = projection.DeepClone() } }.ToJsonString(),
            ProvisioningRuntimeProfile.Resolve("aws-eks"), "eks-dev", "dev", out facts, out _);

    private static JsonNode EksProjection() => JsonNode.Parse("""
        {"schemaVersion":"honua.runtime-binding/v1","runtimeKind":"aws-eks","targetKind":"Kubernetes",
         "backend":"honua-kubernetes-deployment","targetId":"eks-dev","environment":"dev",
         "platform":{"accountId":"123456789012","region":"us-west-2","partition":"aws"},
         "resourceIdentity":{"clusterArn":"arn:aws:eks:us-west-2:123456789012:cluster/honua-dev",
           "apiServer":"https://cluster.eks.amazonaws.com","caSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
           "namespace":"honua","deploymentName":"honua-server","containerName":"server","serviceName":"honua-server",
           "servicePort":"443","targetPort":"8080","routeKind":"LoadBalancerService","routeName":"honua-server",
           "routeHost":"honua.example.com","publicUrl":"https://honua.example.com",
           "loadBalancerArn":"arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/0123456789abcdef",
           "serverServiceAccountName":"honua-server","gpServiceAccountName":"honua-gp",
           "serverRoleArn":"arn:aws:iam::123456789012:role/honua-server","gpRoleArn":"arn:aws:iam::123456789012:role/honua-gp",
           "serverPodIdentityAssociationId":"a-server123","gpPodIdentityAssociationId":"a-gp123",
           "certificateArn":"arn:aws:acm:us-west-2:123456789012:certificate/12345678-1234-1234-1234-123456789012",
           "dbConnectionSecretArn":"arn:aws:secretsmanager:us-west-2:123456789012:secret:db",
           "adminPasswordSecretArn":"arn:aws:secretsmanager:us-west-2:123456789012:secret:admin",
           "masterKeySecretArn":"arn:aws:secretsmanager:us-west-2:123456789012:secret:master"}}
        """)!;

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
