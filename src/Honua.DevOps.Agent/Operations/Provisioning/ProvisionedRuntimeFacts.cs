using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>Only configured facts read from the exact applied Terraform output; observation remains a separate provider operation.</summary>
internal sealed record ProvisionedRuntimeFacts(string RuntimeKind, string TargetKind, string Backend, string TargetId,
    string Environment, string AccountId, string Region, string Partition, JsonElement ResourceIdentity)
{
    internal const string OutputName = "runtime_binding_contract";

    internal static bool TryReadOutputs(string terraformOutputsJson, ProvisioningRuntimeProfile expected,
        string expectedTargetId, string expectedEnvironment, out ProvisionedRuntimeFacts? facts, out string error)
    {
        facts = null;
        error = "The applied Terraform output does not contain a verified target runtime projection.";
        if (terraformOutputsJson.Length > 4 * 1024 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(terraformOutputsJson, new JsonDocumentOptions { MaxDepth = 32 });
            var outputs = document.RootElement;
            if (outputs.ValueKind != JsonValueKind.Object || HasDuplicateProperties(outputs) ||
                !outputs.TryGetProperty(OutputName, out var output) || output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object ||
                ProvisioningContracts.ValidateRuntimeBinding(value.GetRawText()).Count != 0) return false;
            var kind = value.GetProperty("runtimeKind").GetString()!;
            var backend = value.GetProperty("backend").GetString()!;
            var targetKind = value.GetProperty("targetKind").GetString()!;
            var targetId = value.GetProperty("targetId").GetString()!;
            var environment = value.GetProperty("environment").GetString()!;
            if (kind != expected.RuntimeKind || backend != expected.Backend || targetKind != expected.TargetKind ||
                targetId != expectedTargetId || environment != expectedEnvironment) return false;
            var platform = value.GetProperty("platform");
            var account = platform.GetProperty("accountId").GetString()!;
            var region = platform.GetProperty("region").GetString()!;
            var partition = platform.GetProperty("partition").GetString()!;
            var identity = value.GetProperty("resourceIdentity");
            // A syntactically valid ARN from a different account/region is not this approved cell.
            foreach (var property in identity.EnumerateObject().Where(property => property.Name.EndsWith("Arn", StringComparison.Ordinal)))
            {
                var arn = property.Value.GetString()!.Split(':', 6);
                if (arn.Length != 6 || arn[0] != "arn" || arn[1] != partition || arn[4] != account ||
                    (arn[2] == "iam" ? arn[3] != "" || !arn[5].StartsWith("role/", StringComparison.Ordinal)
                        : arn[3] != region)) return false;
            }
            if (kind == "aws-lambda")
            {
                var function = identity.GetProperty("functionArn").GetString()!;
                var alias = identity.GetProperty("aliasArn").GetString()!;
                if (!alias.StartsWith(function + ":", StringComparison.Ordinal) || alias[(function.Length + 1)..].All(char.IsAsciiDigit) ||
                    identity.GetProperty("apiGatewayIntegrationAliasArn").GetString() != alias) return false;
            }
            facts = new(kind, targetKind, backend, targetId, environment, account, region, partition, identity.Clone());
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Raw provider output may contain unrelated sensitive outputs; never include it in errors.
            return false;
        }
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(HasDuplicateProperties);
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name) || HasDuplicateProperties(property.Value));
    }
}
