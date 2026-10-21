using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Joins configured native EKS output to the exact executor's observed post-apply
/// state. It is never an IaC output and never asserts live workload readiness.
/// </summary>
internal static class NativeEksExecutionSeal
{
    internal static string? Build(ProvisioningLineage lineage, IacExecutionEvidence execution,
        string execReceiptSha256, string operatorContractDigest, ProvisionedRuntimeFacts facts,
        string imageDigest, string endpoint, string adminSecretReference)
    {
        if (facts.RuntimeKind != "aws-eks" || facts.TargetKind != "Kubernetes" ||
            facts.Backend != "honua-kubernetes-deployment" || !execution.ReleaseQualified ||
            execution.EvidenceMode != "live" || !execution.Backend.IsRemote ||
            execution.State.LineageAfter is not { } stateLineage || !Guid.TryParse(stateLineage, out _) ||
            execution.State.SerialAfter is not { } stateSerial || stateSerial < 0 ||
            execution.OperatorContractDigest != operatorContractDigest ||
            execution.PlanMetadataDigest != lineage.PlanMetadataDigest ||
            lineage.ActuatorReceiptReference != "urn:sha256:" + execReceiptSha256 ||
            string.IsNullOrWhiteSpace(execution.Backend.BackendConfigDigest) ||
            string.IsNullOrWhiteSpace(execution.Backend.ObjectKey) ||
            !imageDigest.StartsWith("sha256:", StringComparison.Ordinal) || imageDigest.Length != 71 ||
            !imageDigest[7..].All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(adminSecretReference)) return null;

        return JsonSerializer.Serialize(new
        {
            schemaVersion = "honua.devops.native-eks-execution-seal/v1",
            provisioningOperationId = lineage.ProvisioningOperationId,
            planMetadataDigest = lineage.PlanMetadataDigest,
            approvalReceiptSha256 = lineage.ApprovalReceiptSha256,
            applyReceiptSha256 = execReceiptSha256,
            operatorContractDigest,
            configuredRuntimeBindingSha256 = Sha(JsonSerializer.Serialize(facts)),
            stateLineageAfter = stateLineage,
            stateSerialAfter = stateSerial,
            execution.Backend.BackendConfigDigest,
            execution.Backend.Workspace,
            execution.Backend.ObjectKey,
            execution.IacRevision,
            execution.ProviderLockDigest,
            imageDigest,
            endpoint,
            adminSecretReferenceSha256 = Sha(adminSecretReference),
            evidenceMode = "exact-executor-post-state"
        });
    }

    internal static bool Matches(ProvisioningEvidenceStore store, ProvisioningLineage lineage,
        IacExecutionEvidence? execution, string? execReceiptSha256, string? operatorContractDigest,
        ProvisionedRuntimeFacts? facts, string? imageDigest, string? endpoint, string? adminSecretReference)
    {
        if (execution is null || execReceiptSha256 is null || operatorContractDigest is null ||
            facts is null || imageDigest is null || endpoint is null || adminSecretReference is null) return false;
        var expected = Build(lineage, execution, execReceiptSha256, operatorContractDigest, facts,
            imageDigest, endpoint, adminSecretReference);
        var references = lineage.EvidenceRefs?.Where(reference => reference.Kind == "eks-execution-seal").ToArray() ?? [];
        if (expected is null || references.Length != 1) return false;
        try { return store.Read(references[0]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Sha(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
