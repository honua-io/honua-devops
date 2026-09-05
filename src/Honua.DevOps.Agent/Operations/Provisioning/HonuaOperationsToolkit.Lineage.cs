namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    private static async Task<FileStream> AcquireLineageLockAsync(string operationId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetProvisioningStateRoot());
        ProtectDirectory(GetProvisioningStateRoot());
        string path = GetProvisioningStatePath(operationId) + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { await Task.Delay(50, cancellationToken); }
        }
    }

    private static async Task RestoreVerificationAsync(ProvisioningState state, string directory, CancellationToken cancellationToken)
    {
        ProvisioningEvidenceStore store = GetEvidenceStore();
        // Read both before exporting either: a corrupt binding must not refresh a
        // verification receipt that would appear to certify it.
        byte[] receipt = store.Read(state.VerificationReceipt!);
        byte[] binding = store.Read(state.ProvisionBinding!);
        await WriteEvidenceCopyAsync(Path.Combine(directory, "honua-install-verification.receipt.json"), receipt, cancellationToken);
        await WriteEvidenceCopyAsync(Path.Combine(directory, "honua-devops-aws-ecs-provision-binding.json"), binding, cancellationToken);
    }

    private static async Task WriteEvidenceCopyAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporary = destination + $".{Guid.NewGuid():n}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            ProtectSavedPlan(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static OperationResponse VerifiedLineageResponse(ProvisioningLineage lineage, IReadOnlyList<OperationBackendStep> steps)
        => new("install-handoff-verified", "Recovered the original verified handoff receipt.",
            ["The persisted verification identity and evidence bytes are unchanged; probes were not repeated."],
            ["Join the typed evidence references into the release receipt."],
            ["retained evidence digests verified"],
            ["This is historical verification evidence, not a fresh health check."],
            ProvisioningLineage: lineage, BackendSteps: steps);
}
