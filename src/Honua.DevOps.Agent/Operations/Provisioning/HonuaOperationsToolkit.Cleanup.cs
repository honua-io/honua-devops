using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    private static ProvisioningCleanupStore GetCleanupStore() =>
        new(Path.Combine(GetProvisioningStateRoot(), "cleanup-obligations"), GetEvidenceStore());

    // Stdio operator ownership is the protected local OS account plus host-selected target/config.
    // Execution mode/tier are excluded: inspecting one's retained evidence grants no write authority.
    private string CleanupHostBinding() => ProvisioningCleanupStore.Digest(JsonSerializer.SerializeToUtf8Bytes(new
    {
        owner = Environment.UserName,
        domain = Environment.UserDomainName,
        runtime.RuntimeKind,
        runtime.RuntimeBackend,
        runtime.DeployTargetId,
        runtime.ServerTargetId,
        runtime.TerraformRepository,
        runtime.TerraformRef,
        root = Path.GetFullPath(runtime.TerraformLocalPath),
        runtime.CandidateReference
    }));

    private ProvisioningCleanupObligation CreateCleanupObligation(TerraformExactSubstrate substrate,
        SavedTerraformPlan saved, IReadOnlyList<ProvisioningEvidenceReference> originals)
    {
        ProvisioningCleanupStore store = GetCleanupStore();
        string helper = Path.Combine(substrate.IacRoot, "scripts", "lib", "tf-exec-contract.sh");
        ProvisioningCleanupStore.RejectLink(substrate.ApplyScript);
        if (File.Exists(helper)) ProvisioningCleanupStore.RejectLink(helper);
        ProvisioningCleanupObligation origin = new(ProvisioningCleanupStore.Schema,
            saved.Manifest.ProvisioningOperationId, saved.Manifest.Stack, saved.Manifest.Environment,
            CleanupHostBinding(), saved.PlanFile, substrate.IacRoot, substrate.ApplyScript,
            ComputeSha256(substrate.ApplyScript), File.Exists(helper) ? ComputeSha256(helper) : null,
            store.ClaimFor(saved.Manifest.ProvisioningOperationId), saved.Manifest.Metadata,
            [.. originals,
                GetEvidenceStore().Put("cleanup-plan-schema", File.ReadAllBytes(substrate.ExactPlanSchemaPath)),
                GetEvidenceStore().Put("cleanup-exec-schema", File.ReadAllBytes(substrate.ExecReceiptSchemaPath))]);
        store.Create(origin); // Durable failure here prevents the wrapper from starting.
        return origin;
    }

    private async Task<byte[]?> ReadCleanupClaimAsync(ProvisioningCleanupObligation origin)
    {
        GetCleanupStore().ValidateClaimPath(origin);
        if (!Directory.Exists(origin.ClaimDirectory)) return null;
        ProvisioningCleanupStore.RejectLink(Path.GetDirectoryName(origin.ClaimDirectory)!);
        ProvisioningCleanupStore.RejectLink(origin.ClaimDirectory);
        string helper = Path.Combine(origin.IacRoot, "scripts", "lib", "tf-exec-contract.sh");
        if (origin.HelperSha256 is null || !File.Exists(origin.ApplyScript) || !File.Exists(helper)) return null;
        ProvisioningCleanupStore.RejectLink(origin.ApplyScript);
        ProvisioningCleanupStore.RejectLink(helper);
        if (ComputeSha256(origin.ApplyScript) != origin.ApplyScriptSha256 || ComputeSha256(helper) != origin.HelperSha256)
            throw new InvalidDataException("Retained cleanup status substrate changed.");
        ProvisioningProcessResult result = await (provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance)
            .RunAsync("bash", [origin.ApplyScript, "--plan", origin.PlanPath, "--claim-dir", origin.ClaimDirectory, "--claim-status"],
                origin.IacRoot, TimeSpan.FromSeconds(5), new Dictionary<string, string>(), CancellationToken.None);
        if (ComputeSha256(origin.ApplyScript) != origin.ApplyScriptSha256 || ComputeSha256(helper) != origin.HelperSha256)
            throw new InvalidDataException("Cleanup status substrate changed during observation.");
        if (!result.Succeeded) return null;
        byte[] bytes = Encoding.UTF8.GetBytes(result.StandardOutput);
        if (bytes.Length > 64 * 1024) throw new InvalidDataException("Cleanup claim status is oversized.");
        return bytes;
    }

    private async Task CaptureCleanupTerminalAsync(ProvisioningCleanupObligation origin, string receiptPath,
        int? exitCode, bool exited)
    {
        try
        {
            GetCleanupStore().ValidateClaimPath(origin);
            byte[]? committed = ProvisioningCleanupStore.ReadOriginal(Path.Combine(origin.ClaimDirectory, "receipt.json"));
            byte[]? outward = ProvisioningCleanupStore.ReadOriginal(receiptPath);
            if (committed is not null && outward is not null && !committed.AsSpan().SequenceEqual(outward))
                throw new InvalidDataException("Original cleanup receipt copies disagree.");
            byte[]? claim = await ReadCleanupClaimAsync(origin);
            GetCleanupStore().Capture(origin, exitCode, exited, committed ?? outward, claim);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // A finite refusal is durable too; restoring substituted files cannot erase its observation.
            GetCleanupStore().Capture(origin, exitCode, exited, null, null, evidenceInvalid: true);
            throw;
        }
    }

    private async Task<OperationResponse> InspectProvisioningCleanupAsync(string stack, string operation)
    {
        try
        {
            ProvisioningCleanupStore store = GetCleanupStore();
            ProvisioningCleanupObligation origin = store.Read(operation);
            if (origin.HostBinding != CleanupHostBinding() || origin.Stack != NormalizeToken(stack)
                || origin.Stack != ProvisioningRuntimeProfile.Resolve(runtime.RuntimeKind).StackId
                || !runtime.AllowedEnvironments.Contains(origin.Environment, StringComparer.OrdinalIgnoreCase))
                return ProvisioningRefusal("cleanup-inspection-not-authorized", "The retained obligation is outside this operator's host target/environment scope.", [], []);
            byte[]? claim = await ReadCleanupClaimAsync(origin);
            bool live = Directory.Exists(origin.ClaimDirectory) && ProvisioningCleanupStore.ExecutorIsLive(origin.ClaimDirectory);
            byte[]? receipt = ProvisioningCleanupStore.ReadOriginal(Path.Combine(origin.ClaimDirectory, "receipt.json"));
            ProvisioningCleanupEvidence inspection = store.Inspect(origin, receipt, claim, live);
            return new OperationResponse(inspection.State, "Retained cleanup evidence inspected without executing Terraform or changing resource state.",
                ["Cleanup remains outstanding; execution acknowledgement is not an independent resource-absence verdict."],
                ["Use a separately reviewed, action-bound destroy plan and independent resource inventory before claiming cleanup."],
                ["original retained plan, metadata, approval, execution and claim bindings rejudged"],
                ["No cloud observation, automatic destroy, release qualification or readiness is granted."])
            { ProvisioningCleanup = inspection };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return ProvisioningRefusal("cleanup-evidence-invalid", "Retained cleanup evidence is missing, changed, ambiguous or unreadable; no mutation or cleanup claim was made.", [], []);
        }
    }
}
