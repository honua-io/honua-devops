using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>Exact safe registry projection returned by current canonical server authorization.</summary>
internal sealed partial record RegisteredRuntimeAuthority(string ServerTargetId, string DeployTargetId, string Environment,
    string? TenantId, string RuntimeKind, string Backend, string TargetName,
    IReadOnlyDictionary<string, string> ResourceParameters, bool UsesCustomProviderEndpoint)
{
    private static readonly HashSet<string> LambdaKeys = new(StringComparer.Ordinal)
    {
        "aws.region", "lambda.region", "aws.lambda.function_name", "lambda.function_name", "lambda.alias_function_name",
        "aws.lambda.alias_name", "lambda.alias_name", "lambda.alias", "target.resource_id", "lambda.function_arn", "lambda.alias_arn"
    };

    internal static RegisteredRuntimeAuthority ReadLambda(JsonElement response, OperationRuntime runtime, ProvisionedRuntimeFacts facts)
    {
        static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 and <= 1024 } text ? text : throw Refused();
        if (facts.RuntimeKind != "aws-lambda" || runtime.RuntimeKind != facts.RuntimeKind ||
            Text(response, "targetId") != runtime.ServerTargetId || Text(response, "deployTargetId") != runtime.DeployTargetId ||
            Text(response, "deployTargetId") != facts.TargetId || Text(response, "environment") != facts.Environment ||
            Text(response, "runtimeKind") != facts.RuntimeKind || Text(response, "backend") != facts.Backend ||
            !response.TryGetProperty("tenantId", out var tenant) || tenant.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !response.TryGetProperty("usesCustomProviderEndpoint", out var custom) || custom.ValueKind != JsonValueKind.False ||
            !response.TryGetProperty("resourceParameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object) throw Refused();
        var targetName = Text(response, "targetName");
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (!LambdaKeys.Contains(parameter.Name) || parameter.Value.ValueKind != JsonValueKind.String ||
                parameter.Value.GetString() is not { Length: <= 1024 } value || value.Any(char.IsControl) ||
                !registered.TryAdd(parameter.Name, value)) throw Refused();
        }
        string? First(params string[] names) => names.Select(name => registered.GetValueOrDefault(name)?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var function = First("aws.lambda.function_name", "lambda.function_name", "lambda.alias_function_name") ?? targetName;
        var alias = First("aws.lambda.alias_name", "lambda.alias_name", "lambda.alias") ?? "live";
        var region = First("aws.region", "lambda.region");
        if (region is null && First("target.resource_id", "lambda.function_arn", "lambda.alias_arn") is { } resource)
        {
            var parts = resource.Split(':', 6);
            if (parts.Length == 6 && parts[0] == "arn" && parts[2] == "lambda") region = parts[3];
        }
        var functionArn = facts.ResourceIdentity.GetProperty("functionArn").GetString()!;
        var aliasArn = facts.ResourceIdentity.GetProperty("aliasArn").GetString()!;
        // A shadowed ARN pin cannot turn an effective short function name into an
        // account-bound actuator. Match the actual backend precedence, requiring ARN.
        if (function != functionArn || region != facts.Region || aliasArn != functionArn + ":" + alias) throw Refused();
        foreach (var value in registered.Values.Append(targetName).Select(value => value.Trim()).Where(value => value.StartsWith("arn:", StringComparison.Ordinal)))
            if (value != functionArn && value != aliasArn) throw Refused();
        foreach (var name in new[] { "aws.region", "lambda.region" })
            if (registered.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) && value.Trim() != facts.Region) throw Refused();
        var arn = functionArn.Split(':', 6);
        if (arn.Length != 6 || arn[1] != facts.Partition || arn[3] != facts.Region || arn[4] != facts.AccountId) throw Refused();
        var tenantId = tenant.ValueKind == JsonValueKind.Null ? null : tenant.GetString();
        if (tenant.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(tenantId)) throw Refused();
        return new(runtime.ServerTargetId!, facts.TargetId, facts.Environment, tenantId,
            facts.RuntimeKind, facts.Backend, targetName, registered, false);
    }

    private static InvalidDataException Refused() => new("Current canonical registry authority does not bind this exact runtime resource identity.");
}

internal sealed partial class BackendGateway
{
    internal async Task<RegisteredRuntimeAuthority> AuthorizeRuntimeHandoffAsync(OperationRuntime runtime,
        ProvisionedRuntimeFacts facts, string canonicalTool, CancellationToken cancellationToken)
    {
        if (canonicalTool is not ("install_handoff" or "verify_install_handoff"))
            throw new InvalidOperationException("Only canonical handoff authorization is available here.");
        using var result = await PostJsonToHonuaAsync("api/v1/agent/devops/authorize", new
        {
            targetId = runtime.ServerTargetId, deployTargetId = runtime.DeployTargetId, environment = facts.Environment,
            canonicalTool, arguments = new { stack = ProvisioningRuntimeProfile.Resolve(facts.RuntimeKind).StackId }
        }, cancellationToken);
        if (!result.CallResult.IsSuccess || result.Payload is null)
            throw new InvalidDataException("Fresh canonical target authorization is unavailable.");
        return facts.RuntimeKind switch
        {
            "aws-lambda" => RegisteredRuntimeAuthority.ReadLambda(result.Payload.RootElement, runtime, facts),
            "aws-eks" => RegisteredRuntimeAuthority.ReadEks(result.Payload.RootElement, runtime, facts),
            _ => throw new InvalidDataException("Current canonical runtime authorization is unavailable for this runtime.")
        };
    }
}
