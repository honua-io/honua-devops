using System.Globalization;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial record RegisteredRuntimeAuthority
{
    // Match the canonical server's credential-free EKS registry projection. These
    // are current registration pins, never evidence that a workload is serving.
    private static readonly Dictionary<string, string> EksIdentityKeys = new(StringComparer.Ordinal)
    {
        ["kubernetes.cluster_arn"] = "clusterArn",
        ["kubernetes.ca_sha256"] = "caSha256",
        ["kubernetes.namespace"] = "namespace",
        ["kubernetes.deployment_name"] = "deploymentName",
        ["kubernetes.container_name"] = "containerName",
        ["kubernetes.service_name"] = "serviceName",
        ["kubernetes.service_port"] = "servicePort",
        ["kubernetes.target_port"] = "targetPort",
        ["kubernetes.route_kind"] = "routeKind",
        ["kubernetes.route_name"] = "routeName",
        ["kubernetes.route_host"] = "routeHost",
        ["kubernetes.route_path"] = "routePath",
        ["kubernetes.route_path_type"] = "routePathType",
        ["kubernetes.load_balancer_arn"] = "loadBalancerArn",
        ["kubernetes.dns_zone_id"] = "dnsZoneId",
        ["kubernetes.dns_record_name"] = "dnsRecordName"
    };

    private static readonly string[] EksUidKeys =
    [
        "kubernetes.cluster_uid", "kubernetes.deployment_uid",
        "kubernetes.service_uid", "kubernetes.route_uid"
    ];

    internal static RegisteredRuntimeAuthority ReadEks(JsonElement response, OperationRuntime runtime, ProvisionedRuntimeFacts facts)
    {
        static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 and <= 1024 } text &&
            !text.Any(char.IsControl) ? text : throw Refused();

        if (facts.RuntimeKind != "aws-eks" || facts.TargetKind != "Kubernetes" || facts.Backend != "honua-kubernetes-deployment" ||
            runtime.RuntimeKind != facts.RuntimeKind || runtime.RuntimeBackend != facts.Backend ||
            Text(response, "targetId") != runtime.ServerTargetId || Text(response, "deployTargetId") != runtime.DeployTargetId ||
            Text(response, "deployTargetId") != facts.TargetId || Text(response, "environment") != facts.Environment ||
            Text(response, "runtimeKind") != facts.RuntimeKind || Text(response, "backend") != facts.Backend ||
            !response.TryGetProperty("tenantId", out var tenant) || tenant.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !response.TryGetProperty("usesCustomProviderEndpoint", out var custom) || custom.ValueKind != JsonValueKind.False ||
            !response.TryGetProperty("resourceParameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            throw Refused();

        var names = new HashSet<string>(StringComparer.Ordinal);
        if (response.EnumerateObject().Any(property => !names.Add(property.Name))) throw Refused();
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (!EksIdentityKeys.ContainsKey(parameter.Name) && !EksUidKeys.Contains(parameter.Name, StringComparer.Ordinal) ||
                parameter.Value.ValueKind != JsonValueKind.String || parameter.Value.GetString() is not { Length: > 0 and <= 1024 } value ||
                value.Any(char.IsControl) || !registered.TryAdd(parameter.Name, value)) throw Refused();
        }

        var optional = new HashSet<string>(StringComparer.Ordinal)
        {
            "routeHost", "routePath", "routePathType", "loadBalancerArn", "dnsZoneId", "dnsRecordName"
        };
        foreach (var (key, factName) in EksIdentityKeys)
        {
            var hasFact = facts.ResourceIdentity.TryGetProperty(factName, out var fact);
            var hasRegistration = registered.TryGetValue(key, out var registeredValue);
            if (!hasFact && !hasRegistration && optional.Contains(factName)) continue;
            if (!hasFact || !hasRegistration) throw Refused();
            var expected = fact.ValueKind switch
            {
                JsonValueKind.String => fact.GetString(),
                JsonValueKind.Number when (factName is "servicePort" or "targetPort") && fact.TryGetInt32(out var port) && port is > 0 and <= 65535
                    => port.ToString(CultureInfo.InvariantCulture),
                _ => throw Refused()
            };
            if (registeredValue != expected) throw Refused();
        }

        foreach (var key in EksUidKeys)
            if (!registered.TryGetValue(key, out var uid) || !Guid.TryParseExact(uid, "D", out var parsed) || parsed == Guid.Empty)
                throw Refused();

        var cluster = registered["kubernetes.cluster_arn"].Split(':', 6);
        if (cluster.Length != 6 || cluster[0] != "arn" || cluster[1] != facts.Partition || cluster[2] != "eks" ||
            cluster[3] != facts.Region || cluster[4] != facts.AccountId || !cluster[5].StartsWith("cluster/", StringComparison.Ordinal) ||
            cluster[5].Length <= "cluster/".Length) throw Refused();
        var tenantId = tenant.ValueKind == JsonValueKind.Null ? null : tenant.GetString();
        if (tenant.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 1024 || tenantId.Any(char.IsControl)))
            throw Refused();
        return new(runtime.ServerTargetId!, facts.TargetId, facts.Environment, tenantId,
            facts.RuntimeKind, facts.Backend, Text(response, "targetName"), registered, false);
    }
}
