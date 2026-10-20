using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class RegisteredEksRuntimeAuthorityTests
{
    private const string Cluster = "arn:aws:eks:us-west-2:123456789012:cluster/honua-dev";
    private const string ClusterUid = "ef872576-5f3d-4cd6-8795-c89f18ff7dde";

    private static OperationRuntime Runtime() => ProvisioningSubstrateFixtures.CreateRuntime("/host/iac", ExecutionMode.Plan, ExecutionTier.Plan) with
    {
        RuntimeKind = "aws-eks", RuntimeBackend = "honua-kubernetes-deployment",
        ServerTargetId = "server-local", DeployTargetId = "eks-dev"
    };

    private static ProvisionedRuntimeFacts Facts() => new("aws-eks", "Kubernetes", "honua-kubernetes-deployment",
        "eks-dev", "dev", "123456789012", "us-west-2", "aws", JsonSerializer.SerializeToElement(new
        {
            clusterArn = Cluster, apiServer = "https://cluster.eks.amazonaws.com", caSha256 = new string('a', 64),
            @namespace = "honua", deploymentName = "honua-server", containerName = "server", serviceName = "honua-server",
            servicePort = "443", targetPort = "8080", routeKind = "LoadBalancerService", routeName = "honua-server",
            publicUrl = "https://honua.example.com", serverServiceAccountName = "honua-server",
            loadBalancerArn = "arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/0123456789abcdef",
            serverRoleArn = "arn:aws:iam::123456789012:role/honua-server",
            gpServiceAccountName = "honua-gp", gpRoleArn = "arn:aws:iam::123456789012:role/honua-gp",
            serverPodIdentityAssociationId = "a-server123", gpPodIdentityAssociationId = "a-gp123",
            certificateArn = "arn:aws:acm:us-west-2:123456789012:certificate/12345678-1234-1234-1234-123456789012",
            dbConnectionSecretArn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:db",
            adminPasswordSecretArn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin",
            masterKeySecretArn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:master"
        }));

    private static JsonObject Response() => new()
    {
        ["targetId"] = "server-local", ["deployTargetId"] = "eks-dev", ["environment"] = "dev",
        ["runtimeKind"] = "aws-eks", ["backend"] = "honua-kubernetes-deployment", ["tenantId"] = null,
        ["targetName"] = "honua-server", ["usesCustomProviderEndpoint"] = false,
        ["resourceParameters"] = new JsonObject
        {
            ["kubernetes.cluster_arn"] = Cluster, ["kubernetes.api_server"] = "https://cluster.eks.amazonaws.com",
            ["kubernetes.ca_sha256"] = new string('a', 64),
            ["kubernetes.cluster_uid"] = ClusterUid, ["kubernetes.namespace"] = "honua",
            ["kubernetes.deployment_name"] = "honua-server", ["kubernetes.container_name"] = "server",
            ["kubernetes.service_name"] = "honua-server", ["kubernetes.service_port"] = "443",
            ["kubernetes.target_port"] = "8080", ["kubernetes.route_kind"] = "LoadBalancerService",
            ["kubernetes.route_name"] = "honua-server", ["kubernetes.public_url"] = "https://honua.example.com",
            ["kubernetes.load_balancer_arn"] = "arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/0123456789abcdef",
            ["kubernetes.deployment_uid"] = "a6955c9a-dafb-4c84-95f9-40a9bc573d32",
            ["kubernetes.service_uid"] = "be08f380-27ad-4688-814d-2b4f96906551",
            ["kubernetes.route_uid"] = "be08f380-27ad-4688-814d-2b4f96906551"
        }
    };

    private static RegisteredRuntimeAuthority Read(JsonObject response, ProvisionedRuntimeFacts? facts = null, OperationRuntime? runtime = null)
        => RegisteredRuntimeAuthority.ReadEks(JsonSerializer.SerializeToElement(response), runtime ?? Runtime(), facts ?? Facts());

    [Fact]
    public void ExactNativeTargetRetainsCurrentTenantAndObservedIdentityPinsWithoutClaimingProviderObservation()
    {
        var response = Response();
        Assert.Null(Read(response).TenantId);
        response["tenantId"] = "tenant-a";
        var result = Read(response);
        Assert.Equal("tenant-a", result.TenantId);
        Assert.Equal(ClusterUid, result.ResourceParameters["kubernetes.cluster_uid"]);
        Assert.Equal("honua-kubernetes-deployment", result.Backend);
        Assert.False(result.UsesCustomProviderEndpoint);
    }

    [Theory]
    [InlineData("kubernetes.cluster_arn")]
    [InlineData("kubernetes.api_server")]
    [InlineData("kubernetes.ca_sha256")]
    [InlineData("kubernetes.namespace")]
    [InlineData("kubernetes.deployment_name")]
    [InlineData("kubernetes.container_name")]
    [InlineData("kubernetes.service_name")]
    [InlineData("kubernetes.service_port")]
    [InlineData("kubernetes.target_port")]
    [InlineData("kubernetes.route_kind")]
    [InlineData("kubernetes.route_name")]
    [InlineData("kubernetes.public_url")]
    [InlineData("kubernetes.load_balancer_arn")]
    [InlineData("kubernetes.cluster_uid")]
    [InlineData("kubernetes.deployment_uid")]
    [InlineData("kubernetes.service_uid")]
    [InlineData("kubernetes.route_uid")]
    public void MissingOrChangedRegistryPinsRefuseBinding(string key)
    {
        var response = Response(); var parameters = (JsonObject)response["resourceParameters"]!;
        parameters[key] = "different";
        Assert.Throws<InvalidDataException>(() => Read(response));
        parameters.Remove(key);
        Assert.Throws<InvalidDataException>(() => Read(response));
    }

    [Theory]
    [InlineData("targetId")]
    [InlineData("deployTargetId")]
    [InlineData("environment")]
    [InlineData("runtimeKind")]
    [InlineData("backend")]
    public void CurrentAuthorityCannotBeTransferredToAnotherScope(string field)
    {
        var response = Response(); response[field] = "other";
        Assert.Throws<InvalidDataException>(() => Read(response));
    }

    [Fact]
    public void MissingTenantCustomEndpointsUnknownParametersAndMalformedUidsAreRefusedWithoutDisclosure()
    {
        var response = Response(); response.Remove("tenantId");
        Assert.Throws<InvalidDataException>(() => Read(response));
        response = Response(); response["usesCustomProviderEndpoint"] = true;
        Assert.Throws<InvalidDataException>(() => Read(response));
        response = Response(); response.Remove("usesCustomProviderEndpoint");
        Assert.Throws<InvalidDataException>(() => Read(response));
        response = Response(); response["resourceParameters"]!["kubernetes.token"] = "private-bearer-marker";
        Assert.DoesNotContain("private-bearer-marker", Assert.Throws<InvalidDataException>(() => Read(response)).Message);
        response = Response(); response["resourceParameters"]!["kubernetes.cluster_uid"] = Guid.Empty.ToString();
        Assert.Throws<InvalidDataException>(() => Read(response));
        Assert.Throws<InvalidDataException>(() => Read(Response(), runtime: Runtime() with { RuntimeBackend = "honua-gitops-kubernetes" }));
        Assert.Throws<InvalidDataException>(() => Read(Response(), facts: Facts() with { AccountId = "999999999999" }));
    }

    [Fact]
    public void OptionalPublicRouteBindingsMustMatchInBothDirections()
    {
        var response = Response();
        response["resourceParameters"]!["kubernetes.route_host"] = "honua.example.com";
        Assert.Throws<InvalidDataException>(() => Read(response));
        var identity = JsonNode.Parse(Facts().ResourceIdentity.GetRawText())!;
        identity["routeHost"] = "honua.example.com";
        var facts = Facts() with { ResourceIdentity = JsonSerializer.SerializeToElement(identity) };
        Assert.Equal("honua.example.com", Read(response, facts).ResourceParameters["kubernetes.route_host"]);
        Assert.Throws<InvalidDataException>(() => Read(Response(), facts));
    }

    [Fact]
    public void ExternallyManagedDnsCanComeFromCurrentRegistryOnlyAsAPairedRoutePin()
    {
        var response = Response();
        response["resourceParameters"]!["kubernetes.dns_zone_id"] = "Z123456";
        Assert.Throws<InvalidDataException>(() => Read(response));
        response["resourceParameters"]!["kubernetes.dns_record_name"] = "honua.example.com";
        Assert.Equal("Z123456", Read(response).ResourceParameters["kubernetes.dns_zone_id"]);
    }

    [Fact]
    public void DuplicateRegistryMembersAreNotAChoiceOfAuthorities()
    {
        var response = Response().ToJsonString().Replace("\"targetId\":\"server-local\"", "\"targetId\":\"other\",\"targetId\":\"server-local\"", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(response);
        Assert.Throws<InvalidDataException>(() => RegisteredRuntimeAuthority.ReadEks(document.RootElement, Runtime(), Facts()));
    }
}
