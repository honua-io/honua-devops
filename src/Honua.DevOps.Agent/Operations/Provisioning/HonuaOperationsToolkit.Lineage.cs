using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    public async Task<OperationResponse> ProvisionInfrastructureAsync(
        string stack, string size, string action, string variablesJson, bool confirmed,
        string confirmation, string approvalReceiptJson = "", CancellationToken cancellationToken = default,
        string idempotencyKey = "")
    {
        // An unkeyed plan explicitly starts new work. A keyed plan reserves its identity
        // durably BEFORE starting the substrate and can never silently replan on retry.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || NormalizeToken(action) == "apply"
            || (NormalizeToken(action) == "destroy" && confirmed))
            return await ProvisionInfrastructureCoreAsync(stack, size, action, variablesJson, confirmed,
                confirmation, approvalReceiptJson, cancellationToken);

        string requestKey = "plan-request:" + ComputeSha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            root = runtime.TerraformLocalPath, idempotencyKey
        })));
        using FileStream requestLock = await AcquireLineageLockAsync(requestKey, cancellationToken);
        string path = GetProvisioningStatePath(requestKey);
        string requestDigest = ComputeSha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            stack, size, action, variablesJson, confirmed, confirmation
        })));
        if (File.Exists(path))
        {
            PlanRequestReservation reservation = JsonSerializer.Deserialize<PlanRequestReservation>(await File.ReadAllTextAsync(path, cancellationToken))!;
            if (reservation.RequestDigest != requestDigest)
                return ProvisioningRefusal("idempotency-conflict", "The plan idempotency key is already bound to a different request.", [], []);
            if (reservation.Response is not null)
                return reservation.Response with { AuditEventId = Guid.NewGuid().ToString("n") };
            return new("plan-indeterminate", "The reserved plan did not durably record a result; reconcile its artifacts before starting new work.",
                [], [], ["no second plan started"], [],
                ProvisioningLineage: new($"urn:honua:provisioning:{reservation.PlanToken}"));
        }

        PlanRequestReservation pending = new(requestDigest, Guid.NewGuid().ToString("n"), null);
        await WriteEvidenceCopyAsync(path, JsonSerializer.SerializeToUtf8Bytes(pending), cancellationToken);
        OperationResponse response = await ProvisionInfrastructureCoreAsync(stack, size, action, variablesJson,
            confirmed, confirmation, approvalReceiptJson, cancellationToken, pending.PlanToken);
        await WriteEvidenceCopyAsync(path, JsonSerializer.SerializeToUtf8Bytes(pending with { Response = response }), cancellationToken);
        return response;
    }

    private sealed record PlanRequestReservation(string RequestDigest, string PlanToken, OperationResponse? Response);

    private static async Task<FileStream> AcquireLineageLockAsync(string operationId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetProvisioningStateRoot());
        ProtectDirectory(GetProvisioningStateRoot());
        string path = GetProvisioningStatePath(operationId) + ".lock";
        System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(50, cancellationToken);
            }
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
            using (FileStream stream = new(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
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
