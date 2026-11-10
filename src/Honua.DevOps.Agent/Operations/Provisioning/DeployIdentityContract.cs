using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Honua.DevOps.Agent.Operations;

internal sealed record ProvisioningTopology(
    [property: JsonPropertyName("compute")] string Compute,
    [property: JsonPropertyName("redis")] bool Redis,
    [property: JsonPropertyName("custom_domain")] bool CustomDomain,
    [property: JsonPropertyName("waf")] bool Waf,
    [property: JsonPropertyName("gp_batch")] bool GpBatch)
{
    internal static ProvisioningTopology Ecs(IReadOnlyDictionary<string, object?> variables) => new("ecs",
        variables.GetValueOrDefault("redis_enabled") is true,
        new[] { "domain_name", "alb_certificate_arn", "route53_zone_id" }.Any(key =>
            variables.GetValueOrDefault(key) is string { Length: > 0 }), false, false);
}

/// <summary>Trusted launcher input exported by bootstrap/aws-exec-identity, never a model argument.</summary>
internal sealed record DeployIdentityContract(string Json, string Digest, string RoleArn,
    ProvisioningTopology Topology, IReadOnlyDictionary<string, string[]> ActionsByService)
{
    internal const string PathVariable = "HONUA_DEVOPS_DEPLOY_IDENTITY_CONTRACT_PATH";
    internal const string DigestTag = "honua:deploy-identity-contract-sha256";

    internal static DeployIdentityContract Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"Configure {PathVariable} with the administrator's deploy_identity_contract export.");
        if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Deploy identity contract is too large.");
        string json = File.ReadAllText(path);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        ServerOperationLineage.RejectDuplicateFields(root);
        if (root.GetProperty("schema_version").GetString() != "v1" ||
            root.GetProperty("kind").GetString() != "honua.iac.deploy-identity")
            throw new InvalidDataException("Expected the honua-iac deploy_identity_contract v1 output value.");
        string arn = root.GetProperty("role_arn").GetString()!;
        if (!Regex.IsMatch(arn, "^arn:aws(-us-gov|-cn)?:iam::[0-9]{12}:role/([A-Za-z0-9+=,.@_-]+/)*[A-Za-z0-9+=,.@_-]+$"))
            throw new InvalidDataException("The deploy identity must name an exact IAM role ARN.");
        var topology = JsonSerializer.Deserialize<ProvisioningTopology>(root.GetProperty("topology").GetRawText())!;
        // GetProperty/GetBoolean also refuse omitted flags rather than interpreting them as false.
        foreach (string flag in new[] { "redis", "custom_domain", "waf", "gp_batch" })
            _ = root.GetProperty("topology").GetProperty(flag).GetBoolean();
        if (topology.Compute is not ("ecs" or "eks" or "lambda"))
            throw new InvalidDataException("Unknown compute topology.");
        Dictionary<string, string[]> actions = new(StringComparer.Ordinal);
        foreach (JsonProperty service in root.GetProperty("actions_by_service").EnumerateObject())
        {
            var list = service.Value.EnumerateArray().Select(action => action.GetString()!).ToArray();
            if (list.Length == 0 || list.Any(action => action is null ||
                    !Regex.IsMatch(action, "^[a-z0-9-]+:[A-Za-z0-9]+$") ||
                    !action.StartsWith(service.Name + ":", StringComparison.Ordinal) ||
                    Regex.IsMatch(action, "^iam:(CreateUser|CreateAccessKey|CreateLoginProfile|CreateServiceSpecificCredential)$", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("The contract must contain exact service actions and cannot create long-lived credentials.");
            actions.Add(service.Name, list);
        }
        if (actions.Count == 0) throw new InvalidDataException("The deploy identity action list is empty.");
        return new(json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), arn, topology, actions);
    }

    internal bool MatchesAssumedRole(string arn, string account)
    {
        string[] role = RoleArn.Split(':');
        string roleName = role[5].Split('/')[^1];
        return account == role[4] && Regex.IsMatch(arn,
            "^arn:" + Regex.Escape(role[1]) + ":sts::" + Regex.Escape(account) + ":assumed-role/" +
            Regex.Escape(roleName) + "/[A-Za-z0-9+=,.@_-]+$");
    }
}

internal sealed partial class HonuaOperationsToolkit
{
    private OperationResponse? ReadDeployIdentity(IReadOnlyDictionary<string, object?> variables, out DeployIdentityContract? contract)
    {
        contract = null;
        try
        {
            contract = DeployIdentityContract.Load(runtime.DeployIdentityContractPath);
            if (contract.Topology != ProvisioningTopology.Ecs(variables))
                return ProvisioningRefusal("deploy-identity-topology-mismatch", "The administrator's deployment role contract does not match the requested topology.", [], []);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return ProvisioningRefusal("deploy-identity-contract-required", "A valid administrator-exported deploy_identity_contract is required. No Terraform process was started.",
                ["Export bootstrap/aws-exec-identity deploy_identity_contract to the launcher-configured absolute path; assume its role with a short-lived session."], []);
        }
    }

    private async Task<OperationResponse?> CheckDeployCallerAsync(DeployIdentityContract contract, IProvisioningProcessRunner runner,
        string directory, CancellationToken cancellationToken)
    {
        ProvisioningProcessResult caller = await runner.RunAsync("aws", ["sts", "get-caller-identity", "--output", "json"],
            directory, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        try
        {
            using JsonDocument identity = JsonDocument.Parse(caller.StandardOutput);
            if (caller.Succeeded && contract.MatchesAssumedRole(identity.RootElement.GetProperty("Arn").GetString()!,
                    identity.RootElement.GetProperty("Account").GetString()!)) return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { }
        return ProvisioningRefusal("deploy-identity-role-required", "The current AWS caller must be a short-lived assumed-role session for the exact role in deploy_identity_contract. No Terraform process was started.",
            ["The trusted launcher must assume the approved role before starting the operator; IAM users, root and broader caller roles are refused."], []);
    }
}
