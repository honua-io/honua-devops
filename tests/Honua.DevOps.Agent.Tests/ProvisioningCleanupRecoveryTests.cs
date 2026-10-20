using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

/// <summary>Local process/retained evidence controls, never an AWS fault-matrix receipt.</summary>
public sealed class ProvisioningCleanupRecoveryTests
{
    [Fact]
    public async Task FailedWrapper_RetainsOriginalsAndPublicReadSurvivesPlanLossAndRestart()
    {
        if (OperatingSystem.IsWindows()) return; // The governed substrate is bash, not a Windows actuator.
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root);
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        string operation = plan.ProvisioningLineage!.ProvisioningOperationId;
        OperationResponse failed = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="),
            ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"));
        Assert.Equal("terraform-apply-failed", failed.Status);
        Assert.True(runner.SawPrewrittenObligation);
        Assert.Equal(1, runner.Mutations);
        Directory.Delete(Path.GetDirectoryName(runner.PlanPath!)!, recursive: true);
        Assert.False(File.Exists(runner.PlanPath));
        HonuaOperationsToolkit restarted = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: runner);
        OperationResponse recovered = await restarted.ProvisionInfrastructureAsync("aws-ecs", "ignored", "inspect-cleanup",
            "not-json", true, "not-an-approval", idempotencyKey: "not-a-reservation", provisioningOperationId: operation);
        Assert.Equal("cleanup-execution-failed", recovered.Status);
        Assert.True(recovered.ProvisioningCleanup!.CleanupOutstanding);
        Assert.False(recovered.ProvisioningCleanup.CandidateQualified);
        Assert.NotNull(recovered.ProvisioningCleanup.ExecutionReceiptSha256);
        Assert.Equal(1, runner.Mutations);
        Assert.True(runner.StatusAfterPlanLoss);
        Assert.DoesNotContain("state_after", JsonSerializer.Serialize(recovered), StringComparison.Ordinal);

        // The PID comes from a real owned child; status JSON flags are not the live-executor oracle.
        using Process sleeper = Process.Start(new ProcessStartInfo("bash") { UseShellExecute = false, ArgumentList = { "-c", "sleep 30" } })!;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(runner.ClaimDirectory!, "executor_pid"), sleeper.Id.ToString());
            OperationResponse live = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "inspect-cleanup",
                "{}", false, "", provisioningOperationId: operation);
            Assert.Equal("cleanup-live-executor", live.Status);
            Assert.Equal(1, runner.Mutations);
        }
        finally { if (!sleeper.HasExited) { sleeper.Kill(entireProcessTree: true); await sleeper.WaitForExitAsync(); } }
    }

    [Theory]
    [InlineData("receipt-committed")]
    [InlineData("preflight")]
    [InlineData("reconciliation-required")]
    public async Task DisconnectCapture_AllowsOnlyOriginalForwardProgressAfterRestart(string finalPhase)
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root) { DisconnectAfterAdmission = true };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true,
                ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply")));
            OperationResponse initial = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "inspect-cleanup", "{}", false, "",
                provisioningOperationId: plan.ProvisioningLineage!.ProvisioningOperationId);
            Assert.Equal("cleanup-live-executor", initial.Status);
        }
        finally { await runner.CompleteDisconnectedAsync(); }
        await File.WriteAllTextAsync(Path.Combine(runner.ClaimDirectory!, "phase"), finalPhase + "\n");
        Directory.Delete(Path.GetDirectoryName(runner.PlanPath!)!, recursive: true);
        HonuaOperationsToolkit restarted = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Observe, ExecutionTier.Observe), gateway, provisioningProcessRunner: runner);
        OperationResponse result = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "inspect-cleanup", "{}", false, "",
            provisioningOperationId: plan.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(finalPhase == "preflight" ? "cleanup-evidence-invalid"
            : finalPhase == "reconciliation-required" ? "cleanup-indeterminate" : "cleanup-execution-failed", result.Status);
        Assert.Equal(1, runner.Mutations);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("claim-path")]
    [InlineData("saved-plan")]
    [InlineData("backend")]
    [InlineData("account")]
    [InlineData("missing-receipt")]
    [InlineData("helper")]
    public async Task PublicRead_RefusesSubstitutedOrMissingOriginalsWithoutAnotherMutation(string change)
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root);
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"));
        string receipt = Path.Combine(runner.ClaimDirectory!, "receipt.json");
        if (change == "missing-receipt") File.Delete(receipt);
        else if (change == "helper") await File.AppendAllTextAsync(Path.Combine(root.Path, "scripts", "lib", "tf-exec-contract.sh"), "\n# changed\n");
        else
        {
            JsonNode doc = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!;
            switch (change)
            {
                case "action": doc["action"] = "destroy"; break;
                case "claim-path": doc["cleanup"]!["claim_dir"] = "/foreign/claim"; break;
                case "saved-plan": doc["cleanup"]!["saved_plan"] = "/foreign/plan"; break;
                case "backend": doc["backend_step"]!["backend_config_digest"] = new string('a', 64); break;
                case "account": doc["workload_identity"]!["account_id"] = "999999999999"; break;
            }
            await File.WriteAllTextAsync(receipt, doc.ToJsonString());
        }
        OperationResponse result = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "inspect-cleanup", "{}", false, "",
            provisioningOperationId: plan.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal("cleanup-evidence-invalid", result.Status);
        Assert.Equal(1, runner.Mutations);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("claim-path")]
    [InlineData("saved-plan")]
    [InlineData("backend")]
    [InlineData("account")]
    public async Task RejudgingOriginals_RejectsCoherentClaimDigestWithWrongApprovedCell(string change)
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root);
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"));
        string originalDirectory = Path.GetDirectoryName(runner.ClaimDirectory!)!;
        ProvisioningCleanupObligation original = JsonSerializer.Deserialize<ProvisioningCleanupObligation>(
            await File.ReadAllBytesAsync(Path.Combine(originalDirectory, "obligation.json")))!;
        string provisioningRoot = Path.GetDirectoryName(Path.GetDirectoryName(originalDirectory)!)!;
        ProvisioningCleanupStore store = new(Path.Combine(root.Path, "isolated-obligations"),
            new ProvisioningEvidenceStore(Path.Combine(provisioningRoot, "evidence")));
        ProvisioningCleanupObligation origin = original with { ClaimDirectory = store.ClaimFor(original.ProvisioningOperationId) };
        store.Create(origin);
        JsonNode receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runner.ClaimDirectory!, "receipt.json")))!;
        receipt["cleanup"]!["claim_dir"] = origin.ClaimDirectory;
        byte[] Bytes() => Encoding.UTF8.GetBytes(receipt.ToJsonString());
        byte[] Claim() => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "v1", kind = "honua.iac.claim-status", claim_dir = origin.ClaimDirectory,
            state = "reconciliation-required", acquired_at_utc = "2026-10-20T00:00:00Z",
            completed_at_utc = "2026-10-20T00:00:01Z", phase = "receipt-committed",
            executor_id = "owned-original", receipt_digest = ProvisioningCleanupStore.Digest(Bytes())
        });
        Assert.Equal("cleanup-execution-failed", store.Inspect(origin, Bytes(), Claim(), false).State);
        switch (change)
        {
            case "action": receipt["action"] = "destroy"; break;
            case "claim-path": receipt["cleanup"]!["claim_dir"] = "/foreign/claim"; break;
            case "saved-plan": receipt["cleanup"]!["saved_plan"] = "/foreign/plan"; break;
            case "backend": receipt["backend_step"]!["backend_config_digest"] = new string('a', 64); break;
            case "account": receipt["workload_identity"]!["account_id"] = "999999999999"; break;
        }
        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => store.Inspect(origin, Bytes(), Claim(), false));
        Assert.Contains(change is "claim-path" or "saved-plan" ? "claim/plan path" : "approved original", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureMismatch_RemainsRefusedAfterOriginalFilesAreRestored()
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root) { MismatchedOutward = true };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"));
        File.Copy(Path.Combine(runner.ClaimDirectory!, "receipt.json"),
            Path.Combine(Path.GetDirectoryName(runner.PlanPath!)!, "exec-receipt.json"), overwrite: true);
        OperationResponse result = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "inspect-cleanup", "{}", false, "",
            provisioningOperationId: plan.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal("cleanup-evidence-invalid", result.Status);
        Assert.Equal(1, runner.Mutations);
    }

    private sealed class RealFailedWrapper : IProvisioningProcessRunner
    {
        private readonly FakeSubstrateRunner _plan = new();
        internal int Mutations { get; private set; }
        internal bool SawPrewrittenObligation { get; private set; }
        internal bool StatusAfterPlanLoss { get; private set; }
        internal bool DisconnectAfterAdmission { get; init; }
        internal bool MismatchedOutward { get; init; }
        private Task<ProvisioningProcessResult>? _disconnected;
        internal async Task CompleteDisconnectedAsync()
        {
            if (_disconnected is null) return;
            await File.WriteAllTextAsync(PlanPath + ".release", "release");
            Assert.Equal(1, (await _disconnected.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        }
        internal string? PlanPath { get; private set; }
        internal string? ClaimDirectory { get; private set; }

        internal RealFailedWrapper(TerraformTestRoot root)
        {
            string canonical = ProvisioningSubstrateFixtures.ReadFixture("cleanup/tf-exec-contract.sh");
            using JsonDocument provenance = JsonDocument.Parse(ProvisioningSubstrateFixtures.ReadFixture("cleanup/provenance.json"));
            byte[] bytes = Encoding.UTF8.GetBytes(canonical);
            Assert.Equal(provenance.RootElement.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            byte[] header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\0");
            Assert.Equal(provenance.RootElement.GetProperty("gitBlob").GetString(), Convert.ToHexStringLower(SHA1.HashData([.. header, .. bytes])));
            Directory.CreateDirectory(Path.Combine(root.Path, "scripts", "lib"));
            File.WriteAllText(Path.Combine(root.Path, "scripts", "lib", "tf-exec-contract.sh"), canonical);
            File.WriteAllText(root.ApplyScript, """
                #!/usr/bin/env bash
                set -euo pipefail
                source "$(dirname "$0")/lib/tf-exec-contract.sh"
                plan= claim= receipt= status=false
                while (($#)); do
                  case "$1" in
                    --plan) plan="$2"; shift 2;;
                    --claim-dir) claim="$2"; shift 2;;
                    --receipt-out) receipt="$2"; shift 2;;
                    --claim-status) status=true; shift;;
                    *) shift;;
                  esac
                done
                if "$status"; then claim_status "$claim"; exit; fi
                test -f "$(dirname "$claim")/obligation.json"
                mkdir "$claim"
                printf '%s\n' "$$" > "$claim/executor_pid"
                printf '%s\n' 'owned-local-test-process' > "$claim/executor_id"
                utc_now > "$claim/acquired"
                claim_phase "$claim" mutation-started
                if [[ -f "$plan.disconnect" ]]; then
                  for ((i=0; i<200; i++)); do [[ -f "$plan.release" ]] && break; sleep 0.05; done
                  test -f "$plan.release"
                fi
                cp "$plan.test-receipt" "$claim/receipt.json"
                claim_phase "$claim" receipt-committed
                claim_complete "$claim"
                cp "$claim/receipt.json" "$receipt"
                exit 1
                """);
        }

        public bool CanRun(string fileName) => _plan.CanRun(fileName);
        public async Task<ProvisioningProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
        {
            if (fileName != "bash" || Path.GetFileName(arguments[0]) != "terraform-exact-apply.sh")
                return await _plan.RunAsync(fileName, arguments, workingDirectory, timeout, environment, cancellationToken);
            ProcessCall call = new(fileName, arguments, workingDirectory, environment);
            if (arguments.Contains("--claim-status")) StatusAfterPlanLoss |= !File.Exists(call.Option("--plan"));
            else
            {
                Mutations++;
                PlanPath = call.Option("--plan")!;
                ClaimDirectory = call.Option("--claim-dir")!;
                SawPrewrittenObligation = File.Exists(Path.Combine(Path.GetDirectoryName(ClaimDirectory)!, "obligation.json"));
                JsonNode receipt = JsonNode.Parse(ProvisioningSubstrateFixtures.ExecReceiptJson)!;
                receipt["exit_status"] = 1;
                receipt["status"] = "failed";
                receipt["cleanup"]!["claim_dir"] = ClaimDirectory;
                receipt["cleanup"]!["saved_plan"] = PlanPath;
                await File.WriteAllTextAsync(PlanPath + ".test-receipt", receipt.ToJsonString(), cancellationToken);
            }
            if (DisconnectAfterAdmission && !arguments.Contains("--claim-status"))
            {
                await File.WriteAllTextAsync(PlanPath + ".disconnect", "owned test client disconnect", cancellationToken);
                _disconnected = SystemProvisioningProcessRunner.Instance.RunAsync(fileName, arguments, workingDirectory, timeout, environment, CancellationToken.None);
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    string phase = Path.Combine(ClaimDirectory!, "phase");
                    if (File.Exists(phase) && (await File.ReadAllTextAsync(phase, cancellationToken)).Trim() == "mutation-started")
                        throw new OperationCanceledException("Owned client disconnected after admission; child continues to its normal exit.");
                    await Task.Delay(25, cancellationToken);
                }
                throw new InvalidOperationException("Owned failed-wrapper admission barrier was not observed.");
            }
            ProvisioningProcessResult result = await SystemProvisioningProcessRunner.Instance.RunAsync(fileName, arguments, workingDirectory, timeout, environment, cancellationToken);
            if (MismatchedOutward && !arguments.Contains("--claim-status"))
                await File.AppendAllTextAsync(call.Option("--receipt-out")!, "\n", cancellationToken);
            return result;
        }
    }
}
