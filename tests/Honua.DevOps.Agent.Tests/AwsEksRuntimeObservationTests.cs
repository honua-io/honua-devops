using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class AwsEksRuntimeObservationTests
{
    private const string Cluster = "arn:aws:eks:us-west-2:123456789012:cluster/honua-dev";
    private const string LoadBalancer = "arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/0123456789abcdef";
    private const string Endpoint = "https://honua.example.com";
    private static readonly string Digest = "sha256:" + new string('a', 64);
    private static readonly string ClusterUid = Guid.NewGuid().ToString();
    private static readonly string DeploymentUid = Guid.NewGuid().ToString();
    private static readonly string ServiceUid = Guid.NewGuid().ToString();
    private static readonly string ReplicaSetUid = Guid.NewGuid().ToString();
    private static readonly string PodUid = Guid.NewGuid().ToString();

    [Theory]
    [InlineData("valid", true)]
    [InlineData("replaced-deployment", false)]
    [InlineData("wrong-image", false)]
    [InlineData("wrong-service-port", false)]
    [InlineData("wrong-route", false)]
    [InlineData("wrong-listener-certificate", false)]
    [InlineData("wrong-irsa", false)]
    [InlineData("wrong-association", false)]
    [InlineData("missing-pod-identity", false)]
    [InlineData("unready-endpoint", false)]
    [InlineData("not-yet-available", false)]
    [InlineData("deployment-advanced", false)]
    public async Task NativeWorkloadIsQualifiedOnlyByFreshProviderAndRouteReads(string scenario, bool accepted)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=EKS observation test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pem = certificate.ExportCertificatePem();
        var ca = Convert.ToBase64String(Encoding.ASCII.GetBytes(pem));
        var caSha = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        var facts = Facts(caSha);
        var runner = new Reader(scenario, ca);
        var observer = new AwsEksRuntimeObservation(runner);
        if (accepted)
        {
            var observed = await observer.ObserveAsync(facts, Authority(caSha), Endpoint, Digest, CancellationToken.None);
            Assert.Equal(ServiceUid, JsonDocument.Parse(observed.EvidenceJson).RootElement.GetProperty("serviceUid").GetString());
            Assert.Equal(64, observed.EvidenceSha256.Length);
            Assert.DoesNotContain("private-provider-secret", observed.EvidenceJson);
            Assert.Contains(runner.Commands, command => command.Contains("describe-pod-identity-association"));
            Assert.Contains(runner.Commands, command => command.Any(arg => arg.Contains("endpointslices", StringComparison.Ordinal)));
        }
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => observer.ObserveAsync(facts, Authority(caSha), Endpoint, Digest, CancellationToken.None));
        Assert.DoesNotContain(runner.Commands.SelectMany(command => command), arg => arg is "apply" or "patch" or "update-service" or "create-role");
    }

    [Theory]
    [InlineData(null, "honua.example.com", true)]
    [InlineData("other.example.com", "honua.example.com", false)]
    [InlineData(null, "other.example.com", false)]
    public async Task RegistryOnlyDnsUsesPublicUrlAndChecksOptionalRouteHost(string? routeHost, string record, bool accepted)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=EKS observation test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var ca = Convert.ToBase64String(Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()));
        var caSha = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        var identity = JsonNode.Parse(Facts(caSha).ResourceIdentity.GetRawText())!;
        identity.AsObject().Remove("dnsZoneId");
        identity.AsObject().Remove("dnsRecordName");
        if (routeHost is null) identity.AsObject().Remove("routeHost");
        else identity["routeHost"] = routeHost;
        var facts = Facts(caSha) with { ResourceIdentity = JsonSerializer.SerializeToElement(identity) };
        var parameters = Authority(caSha).ResourceParameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        parameters["kubernetes.dns_zone_id"] = "Z123456";
        parameters["kubernetes.dns_record_name"] = record;
        var authority = Authority(caSha) with { ResourceParameters = parameters };
        var observer = new AwsEksRuntimeObservation(new Reader("valid", ca));
        if (accepted)
            Assert.NotNull(await observer.ObserveAsync(facts, authority, Endpoint, Digest, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => observer.ObserveAsync(facts, authority, Endpoint, Digest, CancellationToken.None));
    }

    private static ProvisionedRuntimeFacts Facts(string caSha) => new("aws-eks", "Kubernetes", "honua-kubernetes-deployment",
        "eks-dev", "dev", "123456789012", "us-west-2", "aws", JsonSerializer.SerializeToElement(new
        {
            clusterArn = Cluster, apiServer = "https://cluster.eks.amazonaws.com", caSha256 = caSha,
            @namespace = "honua", deploymentName = "honua-server", containerName = "server",
            serviceName = "honua-server", servicePort = "443", targetPort = "8080",
            routeKind = "LoadBalancerService", routeName = "honua-server", routeHost = "honua.example.com",
            publicUrl = Endpoint, dnsZoneId = "Z123456", dnsRecordName = "honua.example.com",
            loadBalancerArn = LoadBalancer,
            serverServiceAccountName = "honua-server", gpServiceAccountName = "honua-gp",
            serverRoleArn = "arn:aws:iam::123456789012:role/honua-server",
            gpRoleArn = "arn:aws:iam::123456789012:role/honua-gp",
            serverPodIdentityAssociationId = "a-server123", gpPodIdentityAssociationId = "a-gp123",
            certificateArn = "arn:aws:acm:us-west-2:123456789012:certificate/12345678-1234-1234-1234-123456789012",
            dbConnectionSecretArn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:db",
            adminPasswordSecretArn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin",
            masterKeySecretArn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:master"
        }));

    private static RegisteredRuntimeAuthority Authority(string caSha) => new("server-target", "eks-dev", "dev", null,
        "aws-eks", "honua-kubernetes-deployment", "honua-server", new Dictionary<string, string>
        {
            ["kubernetes.cluster_uid"] = ClusterUid, ["kubernetes.deployment_uid"] = DeploymentUid,
            ["kubernetes.service_uid"] = ServiceUid, ["kubernetes.route_uid"] = ServiceUid,
            ["kubernetes.ca_sha256"] = caSha
        }, false);

    private sealed class Reader(string scenario, string ca) : IProvisioningProcessRunner
    {
        private int deploymentReads;
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public bool CanRun(string fileName) => fileName is "aws" or "kubectl";
        public Task<ProvisioningProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            object response;
            if (fileName == "aws")
            {
                response = arguments[1] switch
                {
                    "get-caller-identity" => new { account = "123456789012", arn = "arn:aws:sts::123456789012:assumed-role/operator/session" },
                    "describe-cluster" => new { arn = Cluster, endpoint = "https://cluster.eks.amazonaws.com", status = "ACTIVE",
                        ca, oidc = "https://oidc.eks.us-west-2.amazonaws.com/id/TEST" },
                    "list-pod-identity-associations" => Associations(arguments),
                    "describe-pod-identity-association" => Association(arguments),
                    "describe-load-balancers" => new[] { new { arn = LoadBalancer, dns = "lb.example.com", zone = "ZLB123456", scheme = "internet-facing",
                        type = "network", state = "active" } },
                    "describe-listeners" => new[] { new { loadBalancerArn = LoadBalancer, port = 443, protocol = "TLS",
                        certificates = new[] { new { CertificateArn = scenario == "wrong-listener-certificate"
                            ? "arn:aws:acm:us-west-2:123456789012:certificate/aaaaaaaa-1234-1234-1234-123456789012"
                            : "arn:aws:acm:us-west-2:123456789012:certificate/12345678-1234-1234-1234-123456789012" } } } },
                    "get-hosted-zone" => new { id = "/hostedzone/Z123456", @private = false },
                    "list-resource-record-sets" => new { records = new[] { new { Name = "honua.example.com.", Type = "A",
                        AliasTarget = new { DNSName = scenario == "wrong-route" ? "other.example.com" : "lb.example.com.",
                            HostedZoneId = "ZLB123456" } } }, truncated = false, nextName = "" },
                    _ => throw new InvalidOperationException("Unexpected AWS read")
                };
            }
            else
            {
                Assert.Contains("--kubeconfig", arguments);
                Assert.Equal("get", arguments[3]);
                var path = arguments[5];
                response = path switch
                {
                    "/api/v1/namespaces/kube-system" => new { metadata = new { uid = ClusterUid } },
                    "/apis/apps/v1/namespaces/honua/deployments/honua-server" => Deployment(),
                    "/api/v1/namespaces/honua/serviceaccounts/honua-server" => ServiceAccount("honua-server"),
                    "/api/v1/namespaces/honua/serviceaccounts/honua-gp" => ServiceAccount("honua-gp"),
                    "/apis/apps/v1/namespaces/kube-system/daemonsets/eks-pod-identity-agent" => new
                    {
                        status = new { desiredNumberScheduled = 2, numberReady = 2 }
                    },
                    "/api/v1/namespaces/honua/services/honua-server" => Service(),
                    "/api/v1/namespaces/honua/pods/honua-server-pod" => Pod(),
                    "/apis/apps/v1/namespaces/honua/replicasets/honua-server-rs" => new
                    {
                        metadata = new { uid = ReplicaSetUid, ownerReferences = new[] { new { kind = "Deployment", uid = DeploymentUid } } }
                    },
                    _ when path.StartsWith("/apis/discovery.k8s.io/v1/namespaces/honua/endpointslices?", StringComparison.Ordinal)
                        => new { items = new[] { new { metadata = new { ownerReferences = new[] { new { uid = ServiceUid } } },
                            endpoints = new[] { new { conditions = new { ready = scenario != "unready-endpoint" },
                                targetRef = new { kind = "Pod", name = "honua-server-pod", uid = PodUid } } } } } },
                    _ => throw new InvalidOperationException("Unexpected Kubernetes read")
                };
            }
            return Task.FromResult(new ProvisioningProcessResult(0, JsonSerializer.Serialize(response), "private-provider-secret", false));
        }

        private static object Associations(IReadOnlyList<string> arguments)
        {
            var name = arguments[Array.IndexOf(arguments.ToArray(), "--service-account") + 1];
            return new[] { new { id = name == "honua-server" ? "a-server123" : "a-gp123",
                @namespace = "honua", serviceAccount = name } };
        }

        private object Association(IReadOnlyList<string> arguments)
        {
            var id = arguments[Array.IndexOf(arguments.ToArray(), "--association-id") + 1];
            var name = id == "a-server123" ? "honua-server" : "honua-gp";
            return new { id, @namespace = "honua", serviceAccount = name,
                role = "arn:aws:iam::123456789012:role/" +
                    (scenario == "wrong-association" && name == "honua-server" ? "other" : name) };
        }

        private object Deployment() => new
        {
            metadata = new { uid = scenario == "replaced-deployment" ? Guid.NewGuid().ToString() : DeploymentUid,
                resourceVersion = scenario == "deployment-advanced" && ++deploymentReads > 1 ? "43" : "42",
                generation = 1, annotations = new Dictionary<string, string> { ["deployment.kubernetes.io/revision"] = "1" } },
            spec = new { replicas = 1, template = new { metadata = new { labels = new { app = "honua" } },
                spec = new { serviceAccountName = "honua-server", containers = new[] { new { name = "server",
                    image = "registry.example/honua@" + (scenario == "wrong-image" ? "sha256:" + new string('b', 64) : Digest),
                    env = new[]
                    {
                        new { name = "ConnectionStrings__DefaultConnection", value = "aws:secretsmanager:arn:aws:secretsmanager:us-west-2:123456789012:secret:db" },
                        new { name = "HONUA_ADMIN_PASSWORD", value = "aws:secretsmanager:arn:aws:secretsmanager:us-west-2:123456789012:secret:admin" },
                        new { name = "Security__ConnectionEncryption__MasterKey", value = "aws:secretsmanager:arn:aws:secretsmanager:us-west-2:123456789012:secret:master" }
                    },
                    ports = new[] { new { containerPort = 8080 } } } } } } },
            status = new { observedGeneration = 1, readyReplicas = 1, updatedReplicas = 1,
                availableReplicas = scenario == "not-yet-available" ? 0 : 1 }
        };

        private object ServiceAccount(string name) => new
        {
            metadata = new { annotations = scenario == "wrong-irsa" && name == "honua-server"
                ? new Dictionary<string, string> { ["eks.amazonaws.com/role-arn"] = "arn:aws:iam::123456789012:role/node" }
                : new Dictionary<string, string>() }
        };

        private object Service() => new
        {
            metadata = new { uid = ServiceUid, resourceVersion = "40", annotations = new Dictionary<string, string>
            {
                ["service.beta.kubernetes.io/aws-load-balancer-ssl-cert"] = "arn:aws:acm:us-west-2:123456789012:certificate/12345678-1234-1234-1234-123456789012",
                ["service.beta.kubernetes.io/aws-load-balancer-ssl-ports"] = "443",
                ["service.beta.kubernetes.io/aws-load-balancer-scheme"] = "internet-facing"
            } },
            spec = new { type = "LoadBalancer", selector = new { app = "honua" },
                ports = new[] { new { port = scenario == "wrong-service-port" ? 8443 : 443,
                    targetPort = 8080, protocol = "TCP" } } },
            status = new { loadBalancer = new { ingress = new[] { new { hostname = "lb.example.com" } } } }
        };

        private object Pod() => new
        {
            metadata = new { uid = PodUid, ownerReferences = new[] { new { kind = "ReplicaSet", name = "honua-server-rs", uid = ReplicaSetUid } } },
            spec = new { serviceAccountName = "honua-server", containers = new[] { new { name = "server", image = "registry.example/honua@" + Digest,
                env = new[]
                {
                    new { name = "AWS_CONTAINER_CREDENTIALS_FULL_URI", value = scenario == "missing-pod-identity"
                        ? "" : "http://169.254.170.23/v1/credentials" },
                    new { name = "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", value = "/var/run/secrets/pods.eks.amazonaws.com/serviceaccount/eks-pod-identity-token" }
                }, volumeMounts = new[] { new { name = "eks-pod-identity-token", mountPath = "/var/run/secrets/pods.eks.amazonaws.com/serviceaccount/" } }
            } }, volumes = new[] { new { name = "eks-pod-identity-token", projected = new { sources = new[]
                { new { serviceAccountToken = new { audience = "pods.eks.amazonaws.com", path = "eks-pod-identity-token" } } } } } } },
            status = new { conditions = new[] { new { type = "Ready", status = "True" } } }
        };
    }
}
