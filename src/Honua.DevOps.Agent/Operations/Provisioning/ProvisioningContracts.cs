using System.Collections.Concurrent;
using System.Reflection;
using Honua.DevOps.Agent.Operations.Eval;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// The published honua-devops provisioning contracts, embedded so every artifact is
/// validated against the same committed file a downstream consumer reads.
/// </summary>
/// <remarks>
/// <para>
/// These schemas are validated on WRITE, not only in tests. An evidence artifact
/// that does not satisfy its own contract must never reach the disk, because
/// everything downstream — the release candidate receipt, the install verifier —
/// treats the artifact's existence as the claim it encodes.
/// </para>
/// <para>
/// The schemas under <c>contracts/</c> are the single source: they ship to
/// consumers and they are embedded here, so there is no second copy to drift.
/// </para>
/// </remarks>
internal static class ProvisioningContracts
{
    internal const string ProvisionBindingResource =
        "Honua.DevOps.Agent.contracts.honua-devops-aws-ecs-provision-binding.schema.json";

    internal const string ProxyHandoffResource =
        "Honua.DevOps.Agent.contracts.honua-mcp-proxy-handoff.v1.schema.json";

    internal const string VerificationReceiptResource =
        "Honua.DevOps.Agent.contracts.honua-devops-install-handoff-verification.v1.schema.json";

    internal const string ProvisionApprovalResource =
        "Honua.DevOps.Agent.contracts.honua-devops-provision-approval.v1.schema.json";

    /// <summary>Every provisioning contract, so tests can sweep them as a set.</summary>
    internal static IReadOnlyList<string> AllResources =>
    [
        ProvisionBindingResource,
        ProxyHandoffResource,
        VerificationReceiptResource,
        ProvisionApprovalResource
    ];

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    internal static string Read(string resourceName)
    {
        return Cache.GetOrAdd(resourceName, static name =>
        {
            using Stream? stream = typeof(ProvisioningContracts).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded provisioning contract `{name}` is missing from the assembly.");
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        });
    }

    internal static IReadOnlyList<string> ValidateProvisionBinding(string documentJson)
        => Validate(documentJson, ProvisionBindingResource);

    internal static IReadOnlyList<string> ValidateProxyHandoff(string documentJson)
        => Validate(documentJson, ProxyHandoffResource);

    internal static IReadOnlyList<string> ValidateVerificationReceipt(string documentJson)
        => Validate(documentJson, VerificationReceiptResource);

    internal static IReadOnlyList<string> ValidateProvisionApproval(string documentJson)
        => Validate(documentJson, ProvisionApprovalResource);

    private static IReadOnlyList<string> Validate(string documentJson, string resourceName)
    {
        try
        {
            return JsonSchemaValidator.Validate(documentJson, Read(resourceName));
        }
        catch (System.Text.Json.JsonException exception)
        {
            // A malformed document is a validation failure, never a thrown surprise
            // in the middle of writing evidence.
            return [$"document is not valid JSON: {exception.Message}"];
        }
    }
}
