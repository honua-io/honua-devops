using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.DevOps.Agent.Operations;

/// <summary>Fresh, read-only AWS and Kubernetes observation of the applied native workload.</summary>
internal sealed class AwsEksRuntimeObservation(IProvisioningProcessRunner runner)
{
    internal async Task<RuntimeProviderObservation> ObserveAsync(ProvisionedRuntimeFacts facts,
        RegisteredRuntimeAuthority authority, string endpoint, string imageDigest, CancellationToken cancellationToken)
    {
        if (facts.RuntimeKind != "aws-eks" || facts.TargetKind != "Kubernetes" ||
            facts.Backend != "honua-kubernetes-deployment" || authority.RuntimeKind != facts.RuntimeKind ||
            authority.DeployTargetId != facts.TargetId || !runner.CanRun("aws") || !runner.CanRun("kubectl") ||
            !imageDigest.StartsWith("sha256:", StringComparison.Ordinal) || imageDigest.Length != 71 ||
            !imageDigest[7..].All(char.IsAsciiHexDigit)) Refuse();

        var identity = facts.ResourceIdentity;
        var clusterArn = Text(identity, "clusterArn");
        var arn = clusterArn.Split(':', 6);
        if (arn.Length != 6 || arn[0] != "arn" || arn[1] != facts.Partition || arn[2] != "eks" ||
            arn[3] != facts.Region || arn[4] != facts.AccountId || !arn[5].StartsWith("cluster/", StringComparison.Ordinal)) Refuse();
        var clusterName = arn[5]["cluster/".Length..];
        var ns = KubernetesName(Text(identity, "namespace"));
        var deploymentName = KubernetesName(Text(identity, "deploymentName"));
        var serviceName = KubernetesName(Text(identity, "serviceName"));
        if (clusterName.Length == 0 || Text(identity, "routeKind") != "LoadBalancerService" ||
            Text(identity, "routeName") != serviceName) Refuse();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != "https" ||
            publicUrl.UserInfo.Length != 0 || publicUrl.Query.Length != 0 || publicUrl.Fragment.Length != 0 ||
            publicUrl.AbsoluteUri.TrimEnd('/') != Text(identity, "publicUrl").TrimEnd('/')) Refuse();

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        using var caller = await AwsAsync(facts, ["sts", "get-caller-identity", "--query", "{account:Account,arn:Arn}"], bounded.Token);
        if (Text(caller.RootElement, "account") != facts.AccountId ||
            !Text(caller.RootElement, "arn").StartsWith("arn:" + facts.Partition + ":", StringComparison.Ordinal)) Refuse();
        using var cluster = await AwsAsync(facts, ["eks", "describe-cluster", "--name", clusterName,
            "--query", "cluster.{arn:arn,endpoint:endpoint,status:status,ca:certificateAuthority.data}"], bounded.Token);
        var observedApi = Text(cluster.RootElement, "endpoint");
        var ca = Text(cluster.RootElement, "ca");
        if (Text(cluster.RootElement, "arn") != clusterArn || Text(cluster.RootElement, "status") != "ACTIVE" ||
            observedApi != Text(identity, "apiServer") || !Uri.TryCreate(observedApi, UriKind.Absolute, out var apiUrl) ||
            apiUrl.Scheme != "https" || apiUrl.UserInfo.Length != 0 || apiUrl.Query.Length != 0 || apiUrl.Fragment.Length != 0)
            Refuse();
        byte[] pem;
        try { pem = Convert.FromBase64String(ca); }
        catch (FormatException) { Refuse(); throw; }
        var roots = new X509Certificate2Collection();
        try { roots.ImportFromPem(Encoding.ASCII.GetString(pem)); }
        catch (CryptographicException) { Refuse(); }
        if (roots.Count != 1 || Convert.ToHexStringLower(SHA256.HashData(roots[0].RawData)) != Text(identity, "caSha256")) Refuse();
        foreach (var root in roots) root.Dispose();

        // The short-lived kubeconfig contains a trust anchor and an exec credential
        // locator, never a bearer token. kubectl obtains the token itself and this
        // process never passes it as an argument or records it in evidence.
        var configPath = Path.Combine(Path.GetTempPath(), "honua-eks-observe-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            var config = new Dictionary<string, object>
            {
                ["apiVersion"] = "v1", ["kind"] = "Config",
                ["clusters"] = new[] { new { name = "observed", cluster = new Dictionary<string, object>
                    { ["server"] = observedApi, ["certificate-authority-data"] = ca } } },
                ["contexts"] = new[] { new { name = "observed", context = new { cluster = "observed", user = "observed" } } },
                ["current-context"] = "observed",
                ["users"] = new[] { new { name = "observed", user = new { exec = new
                    { apiVersion = "client.authentication.k8s.io/v1beta1", command = "aws",
                      args = new[] { "eks", "get-token", "--cluster-name", clusterName, "--region", facts.Region },
                      interactiveMode = "Never" } } } }
            };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config), bounded.Token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            using var system = await KubernetesAsync(configPath, "/api/v1/namespaces/kube-system", bounded.Token);
            MatchPin(authority, "kubernetes.cluster_uid", Text(system.RootElement.GetProperty("metadata"), "uid"));
            using var deployment = await KubernetesAsync(configPath,
                $"/apis/apps/v1/namespaces/{ns}/deployments/{deploymentName}", bounded.Token);
            var deployed = deployment.RootElement;
            MatchPin(authority, "kubernetes.deployment_uid", Text(deployed.GetProperty("metadata"), "uid"));
            var deploymentResourceVersion = Text(deployed.GetProperty("metadata"), "resourceVersion");
            var spec = deployed.GetProperty("spec");
            var status = deployed.GetProperty("status");
            if (deployed.GetProperty("metadata").GetProperty("generation").GetInt64() !=
                    status.GetProperty("observedGeneration").GetInt64() ||
                spec.GetProperty("replicas").GetInt32() < 1 ||
                status.GetProperty("readyReplicas").GetInt32() != spec.GetProperty("replicas").GetInt32() ||
                status.GetProperty("availableReplicas").GetInt32() != spec.GetProperty("replicas").GetInt32() ||
                status.GetProperty("updatedReplicas").GetInt32() != spec.GetProperty("replicas").GetInt32()) Refuse();
            var pod = spec.GetProperty("template").GetProperty("spec");
            var containers = pod.GetProperty("containers").EnumerateArray()
                .Where(c => Text(c, "name") == Text(identity, "containerName")).ToArray();
            if (containers.Length != 1 || !Text(containers[0], "image").EndsWith("@" + imageDigest, StringComparison.Ordinal)) Refuse();
            var environment = containers[0].GetProperty("env").EnumerateArray().ToArray();
            foreach (var (variable, fact) in new[]
            {
                ("ConnectionStrings__DefaultConnection", "dbConnectionSecretArn"),
                ("HONUA_ADMIN_PASSWORD", "adminPasswordSecretArn"),
                ("Security__ConnectionEncryption__MasterKey", "masterKeySecretArn")
            })
            {
                var selected = environment.Where(entry => Text(entry, "name") == variable).ToArray();
                if (selected.Length != 1 || Text(selected[0], "value") != "aws:secretsmanager:" + Text(identity, fact)) Refuse();
            }
            if (!int.TryParse(Text(identity, "targetPort"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var selectedPort) || selectedPort is < 1 or > 65535) Refuse();
            if (!containers[0].GetProperty("ports").EnumerateArray().Any(p => p.GetProperty("containerPort").GetInt32() == selectedPort)) Refuse();
            var serviceAccount = KubernetesName(Text(identity, "serverServiceAccountName"));
            if (Text(pod, "serviceAccountName") != serviceAccount) Refuse();

            using var account = await KubernetesAsync(configPath,
                $"/api/v1/namespaces/{ns}/serviceaccounts/{serviceAccount}", bounded.Token);
            var gpAccountName = KubernetesName(Text(identity, "gpServiceAccountName"));
            using var gpAccount = await KubernetesAsync(configPath,
                $"/api/v1/namespaces/{ns}/serviceaccounts/{gpAccountName}", bounded.Token);
            if (gpAccountName == serviceAccount || HasIrsaAnnotation(account.RootElement) || HasIrsaAnnotation(gpAccount.RootElement)) Refuse();
            var serverAssociation = await VerifyPodIdentityAssociationAsync(facts, clusterName, ns, serviceAccount,
                Text(identity, "serverRoleArn"), bounded.Token);
            var gpAssociation = await VerifyPodIdentityAssociationAsync(facts, clusterName, ns, gpAccountName,
                Text(identity, "gpRoleArn"), bounded.Token);
            if (serverAssociation == gpAssociation ||
                serverAssociation != Text(identity, "serverPodIdentityAssociationId") ||
                gpAssociation != Text(identity, "gpPodIdentityAssociationId")) Refuse();
            using var agent = await KubernetesAsync(configPath,
                "/apis/apps/v1/namespaces/kube-system/daemonsets/eks-pod-identity-agent", bounded.Token);
            var agentStatus = agent.RootElement.GetProperty("status");
            if (agentStatus.GetProperty("desiredNumberScheduled").GetInt32() < 1 ||
                agentStatus.GetProperty("numberReady").GetInt32() != agentStatus.GetProperty("desiredNumberScheduled").GetInt32()) Refuse();

            using var service = await KubernetesAsync(configPath,
                $"/api/v1/namespaces/{ns}/services/{serviceName}", bounded.Token);
            var serving = service.RootElement;
            var serviceUid = Text(serving.GetProperty("metadata"), "uid");
            var serviceResourceVersion = Text(serving.GetProperty("metadata"), "resourceVersion");
            MatchPin(authority, "kubernetes.service_uid", serviceUid);
            MatchPin(authority, "kubernetes.route_uid", serviceUid);
            var annotations = serving.GetProperty("metadata").GetProperty("annotations");
            if (Text(annotations, "service.beta.kubernetes.io/aws-load-balancer-ssl-cert") != Text(identity, "certificateArn") ||
                Text(annotations, "service.beta.kubernetes.io/aws-load-balancer-ssl-ports") != "443" ||
                Text(annotations, "service.beta.kubernetes.io/aws-load-balancer-scheme") != "internet-facing") Refuse();
            var servingSpec = serving.GetProperty("spec");
            if (Text(servingSpec, "type") != "LoadBalancer") Refuse();
            if (!int.TryParse(Text(identity, "servicePort"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var expectedServicePort) || expectedServicePort is < 1 or > 65535) Refuse();
            var ports = servingSpec.GetProperty("ports").EnumerateArray().Where(p =>
                p.GetProperty("port").GetInt32() == expectedServicePort &&
                p.GetProperty("targetPort").GetInt32() == selectedPort && Text(p, "protocol") == "TCP").ToArray();
            if (ports.Length != 1 || publicUrl.Port != ports[0].GetProperty("port").GetInt32()) Refuse();
            var selector = servingSpec.GetProperty("selector");
            var labels = spec.GetProperty("template").GetProperty("metadata").GetProperty("labels");
            if (!selector.EnumerateObject().Any() || selector.EnumerateObject().Any(p => Text(labels, p.Name) != p.Value.GetString())) Refuse();
            var ingresses = serving.GetProperty("status").GetProperty("loadBalancer").GetProperty("ingress").EnumerateArray().ToArray();
            if (ingresses.Length != 1) Refuse();
            var lbHost = Text(ingresses[0], "hostname").TrimEnd('.');
            if (lbHost.Length > 253 || lbHost.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))) Refuse();
            using var loadBalancers = await AwsAsync(facts, ["elbv2", "describe-load-balancers",
                "--query", "LoadBalancers[].{arn:LoadBalancerArn,dns:DNSName,zone:CanonicalHostedZoneId,scheme:Scheme,type:Type,state:State.Code}"], bounded.Token);
            if (loadBalancers.RootElement.ValueKind != JsonValueKind.Array) Refuse();
            var matchingLoadBalancers = loadBalancers.RootElement.EnumerateArray()
                .Where(lb => Text(lb, "dns").TrimEnd('.') == lbHost).ToArray();
            if (matchingLoadBalancers.Length != 1 || Text(matchingLoadBalancers[0], "scheme") != "internet-facing" ||
                Text(matchingLoadBalancers[0], "type") != "network" || Text(matchingLoadBalancers[0], "state") != "active") Refuse();
            var loadBalancerArn = Text(matchingLoadBalancers[0], "arn");
            var loadBalancerZone = Text(matchingLoadBalancers[0], "zone");
            if (loadBalancerArn != Text(identity, "loadBalancerArn") ||
                loadBalancerZone.Any(c => !char.IsAsciiLetterOrDigit(c))) Refuse();
            var loadBalancerParts = loadBalancerArn.Split(':', 6);
            if (loadBalancerParts.Length != 6 || loadBalancerParts[0] != "arn" ||
                loadBalancerParts[1] != facts.Partition || loadBalancerParts[2] != "elasticloadbalancing" ||
                loadBalancerParts[3] != facts.Region || loadBalancerParts[4] != facts.AccountId ||
                !loadBalancerParts[5].StartsWith("loadbalancer/net/", StringComparison.Ordinal)) Refuse();
            using var listeners = await AwsAsync(facts, ["elbv2", "describe-listeners", "--load-balancer-arn", loadBalancerArn,
                "--query", "Listeners[].{loadBalancerArn:LoadBalancerArn,port:Port,protocol:Protocol,certificates:Certificates}"], bounded.Token);
            if (listeners.RootElement.ValueKind != JsonValueKind.Array ||
                listeners.RootElement.EnumerateArray().Count(listener =>
                    Text(listener, "loadBalancerArn") == loadBalancerArn &&
                    listener.GetProperty("port").GetInt32() == expectedServicePort &&
                    Text(listener, "protocol") == "TLS" &&
                    listener.GetProperty("certificates").EnumerateArray().Any(certificate =>
                        Text(certificate, "CertificateArn") == Text(identity, "certificateArn"))) != 1) Refuse();
            var configuredZone = identity.TryGetProperty("dnsZoneId", out var zone) && zone.ValueKind == JsonValueKind.String
                ? zone.GetString() : null;
            var registeredZone = authority.ResourceParameters.GetValueOrDefault("kubernetes.dns_zone_id");
            var registeredRecord = authority.ResourceParameters.GetValueOrDefault("kubernetes.dns_record_name");
            var routeZone = configuredZone ?? registeredZone;
            var routeRecord = configuredZone is null ? registeredRecord : Text(identity, "dnsRecordName");
            if (routeZone is not null && routeRecord is not null)
            {
                if (routeZone.Length is < 2 or > 64 || routeZone.TrimStart('/').Any(c => !char.IsAsciiLetterOrDigit(c) && c != '/') ||
                    routeRecord.TrimEnd('.') != publicUrl.IdnHost ||
                    Text(identity, "routeHost").TrimEnd('.') != publicUrl.IdnHost) Refuse();
                using var hostedZone = await AwsAsync(facts, ["route53", "get-hosted-zone", "--id", routeZone,
                    "--query", "{id:HostedZone.Id,private:HostedZone.Config.PrivateZone}"], bounded.Token);
                if (Text(hostedZone.RootElement, "id").Split('/').Last() != routeZone.Split('/').Last() ||
                    hostedZone.RootElement.GetProperty("private").ValueKind != JsonValueKind.False) Refuse();
                using var records = await AwsAsync(facts, ["route53", "list-resource-record-sets", "--hosted-zone-id", routeZone,
                    "--start-record-name", routeRecord, "--start-record-type", "A",
                    "--query", "{records:ResourceRecordSets,truncated:IsTruncated,nextName:NextRecordName}"], bounded.Token);
                var dns = records.RootElement;
                if (dns.GetProperty("truncated").ValueKind == JsonValueKind.True &&
                    Text(dns, "nextName").TrimEnd('.') == publicUrl.IdnHost) Refuse();
                var selectedRecords = dns.GetProperty("records").EnumerateArray().Where(record =>
                    Text(record, "Name").TrimEnd('.') == publicUrl.IdnHost &&
                    Text(record, "Type") is "A" or "AAAA" or "CNAME").ToArray();
                if (selectedRecords.Length is < 1 or > 2 || selectedRecords.Any(record =>
                    Text(record, "Type") is not ("A" or "AAAA") ||
                    record.TryGetProperty("SetIdentifier", out _) ||
                    record.TryGetProperty("HealthCheckId", out _) ||
                    record.TryGetProperty("ResourceRecords", out _) ||
                    !record.TryGetProperty("AliasTarget", out var alias) ||
                    Text(alias, "DNSName").TrimEnd('.') != lbHost ||
                    Text(alias, "HostedZoneId") != loadBalancerZone) ||
                    selectedRecords.Select(record => Text(record, "Type")).Distinct(StringComparer.Ordinal).Count() != selectedRecords.Length)
                    Refuse();
            }
            else if (publicUrl.IdnHost != lbHost) Refuse();

            using var slices = await KubernetesAsync(configPath,
                $"/apis/discovery.k8s.io/v1/namespaces/{ns}/endpointslices?labelSelector=" +
                Uri.EscapeDataString("kubernetes.io/service-name=" + serviceName), bounded.Token);
            var readyPods = new HashSet<string>(StringComparer.Ordinal);
            string? observedReplicaSetUid = null;
            foreach (var slice in slices.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!slice.GetProperty("metadata").GetProperty("ownerReferences").EnumerateArray()
                        .Any(owner => Text(owner, "uid") == serviceUid)) Refuse();
                foreach (var destination in slice.GetProperty("endpoints").EnumerateArray())
                {
                    if (destination.GetProperty("conditions").GetProperty("ready").ValueKind != JsonValueKind.True) continue;
                    var reference = destination.GetProperty("targetRef");
                    if (Text(reference, "kind") != "Pod") Refuse();
                    var podName = KubernetesName(Text(reference, "name"));
                    var podUid = Text(reference, "uid");
                    using var livePod = await KubernetesAsync(configPath,
                        $"/api/v1/namespaces/{ns}/pods/{podName}", bounded.Token);
                    var selectedPod = livePod.RootElement;
                    if (Text(selectedPod.GetProperty("metadata"), "uid") != podUid ||
                        Text(selectedPod.GetProperty("spec"), "serviceAccountName") != serviceAccount ||
                        !selectedPod.GetProperty("spec").GetProperty("containers").EnumerateArray().Any(c =>
                            Text(c, "name") == Text(identity, "containerName") &&
                            Text(c, "image").EndsWith("@" + imageDigest, StringComparison.Ordinal)) ||
                        !HasSelectedPodIdentity(selectedPod, Text(identity, "containerName")) ||
                        !selectedPod.GetProperty("status").GetProperty("conditions").EnumerateArray().Any(c =>
                            Text(c, "type") == "Ready" && Text(c, "status") == "True")) Refuse();
                    var owner = selectedPod.GetProperty("metadata").GetProperty("ownerReferences").EnumerateArray().Single();
                    if (Text(owner, "kind") != "ReplicaSet") Refuse();
                    if (observedReplicaSetUid is not null && observedReplicaSetUid != Text(owner, "uid")) Refuse();
                    observedReplicaSetUid = Text(owner, "uid");
                    var replicaSet = KubernetesName(Text(owner, "name"));
                    using var liveReplicaSet = await KubernetesAsync(configPath,
                        $"/apis/apps/v1/namespaces/{ns}/replicasets/{replicaSet}", bounded.Token);
                    if (Text(liveReplicaSet.RootElement.GetProperty("metadata"), "uid") != Text(owner, "uid") ||
                        !liveReplicaSet.RootElement.GetProperty("metadata").GetProperty("ownerReferences").EnumerateArray()
                            .Any(parent => Text(parent, "kind") == "Deployment" &&
                                Text(parent, "uid") == Text(deployed.GetProperty("metadata"), "uid"))) Refuse();
                    readyPods.Add(podUid);
                }
            }
            if (readyPods.Count != spec.GetProperty("replicas").GetInt32() || observedReplicaSetUid is null) Refuse();

            using var finalDeployment = await KubernetesAsync(configPath,
                $"/apis/apps/v1/namespaces/{ns}/deployments/{deploymentName}", bounded.Token);
            using var finalService = await KubernetesAsync(configPath,
                $"/api/v1/namespaces/{ns}/services/{serviceName}", bounded.Token);
            if (Text(finalDeployment.RootElement.GetProperty("metadata"), "uid") != Text(deployed.GetProperty("metadata"), "uid") ||
                Text(finalDeployment.RootElement.GetProperty("metadata"), "resourceVersion") != deploymentResourceVersion ||
                Text(finalService.RootElement.GetProperty("metadata"), "uid") != serviceUid ||
                Text(finalService.RootElement.GetProperty("metadata"), "resourceVersion") != serviceResourceVersion) Refuse();

            var observedAt = DateTimeOffset.UtcNow;
            var observedIdentity = JsonNode.Parse(identity.GetRawText())!.AsObject();
            observedIdentity["clusterUid"] = authority.ResourceParameters["kubernetes.cluster_uid"];
            observedIdentity["deploymentUid"] = authority.ResourceParameters["kubernetes.deployment_uid"];
            observedIdentity["serviceUid"] = serviceUid;
            observedIdentity["routeUid"] = serviceUid;
            observedIdentity["replicaSetUid"] = observedReplicaSetUid;
            observedIdentity["imageDigest"] = imageDigest;
            observedIdentity["observedGeneration"] = status.GetProperty("observedGeneration").GetInt64();
            observedIdentity["availableReplicas"] = status.GetProperty("availableReplicas").GetInt32();
            observedIdentity["updatedReplicas"] = status.GetProperty("updatedReplicas").GetInt32();
            observedIdentity["deploymentResourceVersion"] = deploymentResourceVersion;
            observedIdentity["serviceResourceVersion"] = serviceResourceVersion;
            var evidence = JsonSerializer.Serialize(new
            {
                schemaVersion = "honua.devops.runtime-observation/v1", runtimeKind = facts.RuntimeKind,
                facts.TargetKind, facts.Backend, facts.TargetId, facts.Environment,
                platform = new { facts.AccountId, facts.Region, facts.Partition },
                resourceIdentity = observedIdentity, endpoint, imageDigest,
                clusterUid = authority.ResourceParameters["kubernetes.cluster_uid"],
                deploymentUid = authority.ResourceParameters["kubernetes.deployment_uid"],
                serviceUid, routeUid = serviceUid, deploymentResourceVersion, serviceResourceVersion,
                loadBalancerArn, loadBalancerHost = lbHost,
                serverPodIdentityAssociationId = serverAssociation, gpPodIdentityAssociationId = gpAssociation,
                deploymentRevision = Text(deployed.GetProperty("metadata").GetProperty("annotations"), "deployment.kubernetes.io/revision"),
                observedAtUtc = observedAt,
                scope = "Read-only AWS and Kubernetes configuration, ready workload endpoints, active TLS listener and public route; public authentication and MCP probes are separately required."
            });
            return new(JsonSerializer.SerializeToElement(observedIdentity), observedAt, evidence,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))));
        }
        finally { try { File.Delete(configPath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* No credential is stored in this file. */ } }
    }

    private async Task<string> VerifyPodIdentityAssociationAsync(ProvisionedRuntimeFacts facts, string cluster,
        string ns, string serviceAccount, string roleArn, CancellationToken token)
    {
        var parts = roleArn.Split(':', 6);
        if (parts.Length != 6 || parts[0] != "arn" || parts[1] != facts.Partition || parts[2] != "iam" ||
            parts[3] != "" || parts[4] != facts.AccountId || !parts[5].StartsWith("role/", StringComparison.Ordinal)) Refuse();
        using var listed = await AwsAsync(facts, ["eks", "list-pod-identity-associations", "--cluster-name", cluster,
            "--namespace", ns, "--service-account", serviceAccount,
            "--query", "associations[].{id:associationId,namespace:namespace,serviceAccount:serviceAccount}"], token);
        if (listed.RootElement.ValueKind != JsonValueKind.Array || listed.RootElement.GetArrayLength() != 1) Refuse();
        var id = Text(listed.RootElement[0], "id");
        if (Text(listed.RootElement[0], "namespace") != ns || Text(listed.RootElement[0], "serviceAccount") != serviceAccount) Refuse();
        using var described = await AwsAsync(facts, ["eks", "describe-pod-identity-association", "--cluster-name", cluster,
            "--association-id", id,
            "--query", "association.{id:associationId,namespace:namespace,serviceAccount:serviceAccount,role:roleArn}"], token);
        if (Text(described.RootElement, "id") != id || Text(described.RootElement, "namespace") != ns ||
            Text(described.RootElement, "serviceAccount") != serviceAccount || Text(described.RootElement, "role") != roleArn) Refuse();
        return id;
    }

    private static bool HasIrsaAnnotation(JsonElement account)
        => account.GetProperty("metadata").TryGetProperty("annotations", out var annotations) &&
            annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty("eks.amazonaws.com/role-arn", out _);

    private static bool HasSelectedPodIdentity(JsonElement pod, string containerName)
    {
        var containers = pod.GetProperty("spec").GetProperty("containers").EnumerateArray()
            .Where(c => Text(c, "name") == containerName).ToArray();
        if (containers.Length != 1 || !containers[0].TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Array) return false;
        var entries = env.EnumerateArray().ToArray();
        if (entries.Any(entry => Text(entry, "name") is "AWS_ACCESS_KEY_ID" or "AWS_SECRET_ACCESS_KEY" or
                "AWS_SESSION_TOKEN" or "AWS_WEB_IDENTITY_TOKEN_FILE" or "AWS_ROLE_ARN")) return false;
        string? Value(string name) => entries.Where(entry => Text(entry, "name") == name)
            .Select(entry => entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .SingleOrDefault();
        if (Value("AWS_CONTAINER_CREDENTIALS_FULL_URI") != "http://169.254.170.23/v1/credentials" ||
            Value("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE") != "/var/run/secrets/pods.eks.amazonaws.com/serviceaccount/eks-pod-identity-token")
            return false;
        if (!containers[0].TryGetProperty("volumeMounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array ||
            !mounts.EnumerateArray().Any(m => Text(m, "name") == "eks-pod-identity-token" &&
                Text(m, "mountPath") == "/var/run/secrets/pods.eks.amazonaws.com/serviceaccount/")) return false;
        var spec = pod.GetProperty("spec");
        return spec.TryGetProperty("volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array &&
            volumes.EnumerateArray().Any(v => Text(v, "name") == "eks-pod-identity-token" &&
                v.GetProperty("projected").GetProperty("sources").EnumerateArray().Any(source =>
                    source.TryGetProperty("serviceAccountToken", out var token) &&
                    Text(token, "audience") == "pods.eks.amazonaws.com" &&
                    Text(token, "path") == "eks-pod-identity-token"));
    }

    private async Task<JsonDocument> AwsAsync(ProvisionedRuntimeFacts facts, IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync("aws", [.. args, "--region", facts.Region, "--output", "json", "--no-cli-pager"],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(20),
            new Dictionary<string, string> { ["AWS_PAGER"] = "", ["AWS_CLI_AUTO_PROMPT"] = "off", ["AWS_IGNORE_CONFIGURED_ENDPOINT_URLS"] = "true" }, token);
        return Parse(result);
    }

    private async Task<JsonDocument> KubernetesAsync(string config, string path, CancellationToken token)
    {
        var result = await runner.RunAsync("kubectl", ["--kubeconfig", config, "--request-timeout=15s", "get", "--raw", path],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(20), null, token);
        return Parse(result);
    }

    private static JsonDocument Parse(ProvisioningProcessResult result)
    {
        if (!result.Succeeded || result.StandardOutput.Length > 1024 * 1024) Refuse();
        try { return JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException) { Refuse(); throw; }
    }

    private static string Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
           field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 2048 } text &&
           !text.Any(char.IsControl) ? text : throw new InvalidDataException("The live EKS observation is incomplete.");

    private static string KubernetesName(string name)
    {
        if (name.Length is < 1 or > 253 || !char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]) ||
            name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')) Refuse();
        return name;
    }

    private static void MatchPin(RegisteredRuntimeAuthority authority, string key, string observed)
    {
        if (!authority.ResourceParameters.TryGetValue(key, out var registered) || registered != observed) Refuse();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse() => throw new InvalidDataException("The live EKS workload, route or selected identity does not match the approved and registered target.");
}
