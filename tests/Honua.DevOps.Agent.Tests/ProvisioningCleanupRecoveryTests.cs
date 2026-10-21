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
    public async Task DestroyRecovery_PublicPlanIssuerRestartAndFreshApproval_RetainsOriginalRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        using OwnedEnvironment input = new("TF_VAR_honua_recovery_owned_input", "owned-test-input-not-a-secret");
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root, destroyRecovery: true);
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit Create() => new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.BreakGlass), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        async Task<string> Issue(OperationResponse plan) => (await ApprovalReceiptIssuer.IssueAsync(
            new ApprovalIssueRequest(JsonSerializer.Serialize(plan), "destroy", ProvisioningSubstrateFixtures.ApprovalIssuer,
                ApprovalSigningModes.LocalHmacDev), ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider())).ReceiptJson;
        HonuaOperationsToolkit executor = Create();
        OperationResponse first = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "");
        Assert.Equal("terraform-destroy-plan-ready", first.Status);
        string firstApproval = await Issue(first);
        OperationResponse failed = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(first, "confirmation="), firstApproval);
        Assert.Equal("terraform-destroy-failed", failed.Status);
        string original = first.ProvisioningLineage!.ProvisioningOperationId;
        Directory.Delete(Path.GetDirectoryName(runner.PlanPath!)!, recursive: true);
        HonuaOperationsToolkit restarted = Create();
        OperationResponse retry = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "fresh-destroy-recovery", provisioningOperationId: original);
        Assert.Equal("terraform-destroy-plan-ready", retry.Status);
        Assert.Equal(original, retry.ProvisioningRecovery!.RootProvisioningOperationId);
        Assert.Equal(original, retry.ProvisioningRecovery.ParentProvisioningOperationId);
        Assert.NotEqual(original, retry.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(retry.ProvisioningRecovery.ContextSha256, retry.ApprovalRequest!.RecoveryContextSha256);
        OperationResponse same = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "fresh-destroy-recovery", provisioningOperationId: original);
        Assert.Equal(retry.ProvisioningLineage.ProvisioningOperationId, same.ProvisioningLineage!.ProvisioningOperationId);
        int priorPlans = runner.PlanCalls;
        OperationResponse[] concurrent = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
                idempotencyKey: "fresh-destroy-recovery", provisioningOperationId: original)));
        Assert.All(concurrent, value => Assert.Equal(retry.ProvisioningLineage.ProvisioningOperationId, value.ProvisioningLineage!.ProvisioningOperationId));
        Assert.Equal(priorPlans, runner.PlanCalls);
        Assert.Equal(1, runner.Mutations);
        OperationResponse whitespace = await restarted.ProvisionInfrastructureAsync("AWS-ECS", "SMALL", "destroy", "{ }", false, "",
            idempotencyKey: "fresh-destroy-recovery", provisioningOperationId: original);
        Assert.Equal(retry.ProvisioningLineage.ProvisioningOperationId, whitespace.ProvisioningLineage!.ProvisioningOperationId);
        OperationResponse conflict = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{\"environment\":\"staging\"}", false, "",
            idempotencyKey: "fresh-destroy-recovery", provisioningOperationId: original);
        Assert.Equal("idempotency-conflict", conflict.Status);
        string supersededApproval = await Issue(retry);
        OperationResponse newer = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "explicit-new-recovery-key", provisioningOperationId: original);
        Assert.Equal("terraform-destroy-plan-ready", newer.Status);
        int planned = runner.PlanCalls;
        OperationResponse oldKey = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "fresh-destroy-recovery", provisioningOperationId: original);
        Assert.Equal(retry.ProvisioningLineage.ProvisioningOperationId, oldKey.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(planned, runner.PlanCalls);
        OperationResponse superseded = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(retry, "confirmation="), supersededApproval);
        Assert.Equal("destroy-recovery-refused", superseded.Status);
        Assert.Equal(1, runner.Mutations);
        retry = newer;
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(retry, "confirmation=");
        OperationResponse spent = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true, challenge, firstApproval);
        Assert.NotEqual("infrastructure-destroyed", spent.Status);
        Assert.Equal(1, runner.Mutations);
        string freshApproval = await Issue(retry);
        OperationResponse completed = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true, challenge, freshApproval);
        Assert.Equal("infrastructure-destroyed", completed.Status);
        Assert.Equal(2, runner.Mutations);
        OperationResponse replay = await Create().ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true, challenge, freshApproval);
        Assert.Equal("infrastructure-destroyed", replay.Status);
        Assert.Equal(original, replay.ProvisioningRecovery!.RootProvisioningOperationId);
        Assert.Equal(2, runner.Mutations);
    }

    [Fact]
    public async Task DestroyRecovery_CancelledOriginalWithoutFinalReceipt_RemainsIndeterminateAndCanReplanAfterActualExit()
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root, destroyRecovery: true) { DisconnectAfterAdmission = true };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.BreakGlass), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse first = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "");
        IssuedApprovalReceipt approval = await ApprovalReceiptIssuer.IssueAsync(
            new ApprovalIssueRequest(JsonSerializer.Serialize(first), "destroy", ProvisioningSubstrateFixtures.ApprovalIssuer,
                ApprovalSigningModes.LocalHmacDev), ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider());
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true,
                ProvisioningSubstrateFixtures.ExtractChallenge(first, "confirmation="), approval.ReceiptJson));
            OperationResponse live = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
                idempotencyKey: "while-original-live", provisioningOperationId: first.ProvisioningLineage!.ProvisioningOperationId);
            Assert.Equal("destroy-recovery-refused", live.Status);
        }
        finally { await runner.CompleteDisconnectedAsync(); }
        File.Delete(Path.Combine(runner.ClaimDirectory!, "receipt.json"));
        OperationResponse retry = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "after-owned-original-exit", provisioningOperationId: first.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal("terraform-destroy-plan-ready", retry.Status);
        Assert.Equal(first.ProvisioningLineage.ProvisioningOperationId, retry.ProvisioningRecovery!.RootProvisioningOperationId);
        Assert.Equal(1, runner.Mutations);
    }

    [Fact]
    public async Task DestroyRecovery_InterruptedPlanAfterFailedSuccessor_PreservesImmediateParent()
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root, destroyRecovery: true) { SuccessAfterMutation = int.MaxValue };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit Create() => new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.BreakGlass), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        async Task<OperationResponse> Fail(HonuaOperationsToolkit executor, OperationResponse plan)
        {
            IssuedApprovalReceipt approval = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(JsonSerializer.Serialize(plan), "destroy", ProvisioningSubstrateFixtures.ApprovalIssuer,
                    ApprovalSigningModes.LocalHmacDev), ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider());
            OperationResponse result = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true,
                ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), approval.ReceiptJson);
            Assert.Equal("terraform-destroy-failed", result.Status);
            return result;
        }
        HonuaOperationsToolkit firstSession = Create();
        OperationResponse original = await firstSession.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "");
        await Fail(firstSession, original);
        string rootOperation = original.ProvisioningLineage!.ProvisioningOperationId;
        OperationResponse second = await firstSession.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "first-failed-successor", provisioningOperationId: rootOperation);
        Assert.Equal("terraform-destroy-plan-ready", second.Status);
        await Fail(firstSession, second);
        runner.FailNextPlan = true;
        OperationResponse interrupted = await firstSession.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "interrupted-read-only-plan", provisioningOperationId: rootOperation);
        Assert.Equal("destroy-recovery-refused", interrupted.Status);
        HonuaOperationsToolkit restarted = Create();
        OperationResponse repeated = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "interrupted-read-only-plan", provisioningOperationId: rootOperation);
        Assert.Equal("recovery-plan-indeterminate", repeated.Status);
        OperationResponse fresh = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "",
            idempotencyKey: "explicit-new-after-interruption", provisioningOperationId: rootOperation);
        Assert.Equal("terraform-destroy-plan-ready", fresh.Status);
        Assert.Equal(rootOperation, fresh.ProvisioningRecovery!.RootProvisioningOperationId);
        Assert.Equal(second.ProvisioningLineage!.ProvisioningOperationId, fresh.ProvisioningRecovery.ParentProvisioningOperationId);
        Assert.Equal(2, runner.Mutations);
    }

    [Theory]
    [InlineData("missing-claim")]
    [InlineData("missing-executor")]
    [InlineData("live-executor")]
    [InlineData("role-substitution")]
    [InlineData("serial-backslide")]
    [InlineData("input-substitution")]
    [InlineData("input-order")]
    [InlineData("missing-recovery-projection")]
    [InlineData("missing-recovery-index")]
    [InlineData("missing-both-recovery-projections")]
    [InlineData("altered-recovery-actor")]
    [InlineData("coherent-parent-context-substitution")]
    public async Task DestroyRecovery_OriginalCustodyAndFreshRelationRefusals_StartNoSecondExecutor(string mutation)
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        RealFailedWrapper runner = new(root, destroyRecovery: true);
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.BreakGlass), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        async Task<string> Issue(OperationResponse plan) => (await ApprovalReceiptIssuer.IssueAsync(
            new ApprovalIssueRequest(JsonSerializer.Serialize(plan), "destroy", ProvisioningSubstrateFixtures.ApprovalIssuer,
                ApprovalSigningModes.LocalHmacDev), ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider())).ReceiptJson;
        OperationResponse first = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "");
        Assert.Equal("terraform-destroy-plan-ready", first.Status);
        OperationResponse failed = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true,
            ProvisioningSubstrateFixtures.ExtractChallenge(first, "confirmation="), await Issue(first));
        Assert.Equal("terraform-destroy-failed", failed.Status);
        Process? owned = null;
        string? moved = null;
        try
        {
            switch (mutation)
            {
                case "missing-claim":
                    moved = runner.ClaimDirectory + ".owned-test-move";
                    Directory.Move(runner.ClaimDirectory!, moved);
                    break;
                case "missing-executor": File.Delete(Path.Combine(runner.ClaimDirectory!, "executor_pid")); break;
                case "live-executor":
                    owned = Process.Start(new ProcessStartInfo("bash")
                    { UseShellExecute = false, RedirectStandardInput = true, ArgumentList = { "-c", "read -r owned_release" } })!;
                    await File.WriteAllTextAsync(Path.Combine(runner.ClaimDirectory!, "executor_pid"), owned.Id.ToString());
                    break;
                case "role-substitution": runner.RecoveryMutation = doc => doc["identity"]!["role_id"] = "AROAOTHER"; break;
                case "serial-backslide": runner.RecoveryMutation = doc => doc["state_before"]!["serial"] = 11; break;
                case "input-order": runner.RecoveryMutation = doc =>
                {
                    JsonArray refs = doc["inputs"]!["input_refs"]!.AsArray();
                    JsonNode first = refs[0]!.DeepClone(), second = refs[1]!.DeepClone();
                    refs[0] = second; refs[1] = first;
                }; break;
            }
            OperationResponse retry = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy",
                mutation == "input-substitution" ? "{\"environment\":\"staging\"}" : "{}", false, "",
                idempotencyKey: "negative-recovery", provisioningOperationId: first.ProvisioningLineage!.ProvisioningOperationId);
            if (mutation is "missing-recovery-projection" or "missing-recovery-index" or "missing-both-recovery-projections" or "altered-recovery-actor" or "coherent-parent-context-substitution")
            {
                Assert.Equal("terraform-destroy-plan-ready", retry.Status);
                string operation = retry.ProvisioningLineage!.ProvisioningOperationId;
                if (mutation == "coherent-parent-context-substitution")
                {
                    string stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "honua-devops", "provisioning");
                    ProvisioningEvidenceStore evidence = new(Path.Combine(stateRoot, "evidence"));
                    DestroyRecoveryContext context = JsonSerializer.Deserialize<DestroyRecoveryContext>(evidence.Read(retry.ProvisioningRecovery!.EvidenceRef))!;
                    DestroyRecoveryContext forged = context with { ParentMetadata = context.ParentMetadata with { RoleId = "AROAOTHER" } };
                    ProvisioningEvidenceReference reference = evidence.Put("destroy-recovery", JsonSerializer.SerializeToUtf8Bytes(forged));
                    retry = retry with
                    {
                        ProvisioningRecovery = retry.ProvisioningRecovery with { ContextSha256 = reference.Sha256, EvidenceRef = reference },
                        ApprovalRequest = retry.ApprovalRequest! with { RecoveryContextSha256 = reference.Sha256 }
                    };
                    string manifestPath = Path.Combine(Path.GetTempPath(), "honua-devops-terraform-plans", operation.Split(':')[^1], "manifest.json");
                    JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
                    manifest["Recovery"] = JsonSerializer.SerializeToNode(reference);
                    await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
                    string recoveryRoot = Path.Combine(stateRoot, "destroy-recovery");
                    string index = Path.Combine(recoveryRoot, "attempts", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operation))) + ".json");
                    await File.WriteAllTextAsync(index, JsonSerializer.Serialize(reference));
                    string headPath = Path.Combine(recoveryRoot, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context.RootProvisioningOperationId))) + ".json");
                    DestroyRecoveryHead head = JsonSerializer.Deserialize<DestroyRecoveryHead>(await File.ReadAllTextAsync(headPath))!;
                    await File.WriteAllTextAsync(headPath, JsonSerializer.Serialize(head with { Context = reference }));
                }
                string approval = await Issue(retry);
                if (mutation == "altered-recovery-actor")
                {
                    string manifest = Path.Combine(Path.GetTempPath(), "honua-devops-terraform-plans", operation.Split(':')[^1], "manifest.json");
                    JsonNode saved = JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
                    saved["Metadata"]!["ActorId"] = "honua-devops:" + operation;
                    string metadataPath = saved["PlanMetadataFile"]!.GetValue<string>();
                    JsonNode originalMetadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;
                    originalMetadata["actor_id"] = "honua-devops:" + operation;
                    await File.WriteAllTextAsync(metadataPath, originalMetadata.ToJsonString());
                    await File.WriteAllTextAsync(manifest, saved.ToJsonString());
                }
                if (mutation is "missing-recovery-projection" or "missing-both-recovery-projections")
                {
                    string manifest = Path.Combine(Path.GetTempPath(), "honua-devops-terraform-plans", operation.Split(':')[^1], "manifest.json");
                    JsonNode saved = JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
                    saved.AsObject().Remove("Recovery");
                    await File.WriteAllTextAsync(manifest, saved.ToJsonString());
                }
                if (mutation is "missing-recovery-index" or "missing-both-recovery-projections")
                {
                    string index = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "honua-devops", "provisioning", "destroy-recovery", "attempts",
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operation))) + ".json");
                    File.Delete(index);
                }
                retry = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", true,
                    ProvisioningSubstrateFixtures.ExtractChallenge(retry, "confirmation="), approval);
            }
            // The protected input witness now refuses reordered wrapper refs
            // before a recovery plan can be approved; other mutations reach the
            // original recovery relation guard.
            Assert.Equal(mutation == "input-order" ? "exact-plan-input-mismatch" : "destroy-recovery-refused",
                retry.Status);
            Assert.Equal(1, runner.Mutations);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.StandardInput.WriteLineAsync("normal-owned-exit");
                owned.StandardInput.Close();
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                owned.Dispose();
            }
            if (moved is not null) Directory.Move(moved, runner.ClaimDirectory!);
        }
    }

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
            ExecutionMode.Plan, ExecutionTier.Observe), gateway, provisioningProcessRunner: runner);
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

    private sealed class OwnedEnvironment : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;
        internal OwnedEnvironment(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class RealFailedWrapper : IProvisioningProcessRunner
    {
        private readonly FakeSubstrateRunner _plan = new();
        private readonly bool _destroyRecovery;
        internal Action<JsonNode>? RecoveryMutation { get; set; }
        internal int SuccessAfterMutation { get; set; } = 2;
        internal bool FailNextPlan { get; set; }
        internal int Mutations { get; private set; }
        internal int PlanCalls => _plan.Calls.Count(call => call.Arguments.Count > 0 && Path.GetFileName(call.Arguments[0]) == "terraform-exact-plan.sh");
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

        internal RealFailedWrapper(TerraformTestRoot root, bool destroyRecovery = false)
        {
            _destroyRecovery = destroyRecovery;
            if (destroyRecovery)
            {
                File.WriteAllText(Path.Combine(root.AwsRoot, "owned.auto.tfvars"), "# owned fixture\n");
                File.WriteAllText(Path.Combine(root.AwsRoot, "owned.auto.tfvars.json"), "{}");
            }
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
                if [[ -f "$plan.success" ]]; then exit 0; fi
                exit 1
                """);
        }

        public bool CanRun(string fileName) => _plan.CanRun(fileName);
        public async Task<ProvisioningProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
        {
            if (fileName != "bash" || Path.GetFileName(arguments[0]) != "terraform-exact-apply.sh")
            {
                if (_destroyRecovery && fileName == "bash" && Path.GetFileName(arguments[0]) == "terraform-exact-plan.sh" && FailNextPlan)
                {
                    FailNextPlan = false;
                    throw new IOException("Owned interrupted read-only planning fixture.");
                }
                ProvisioningProcessResult planned = await _plan.RunAsync(fileName, arguments, workingDirectory, timeout, environment, cancellationToken);
                if (_destroyRecovery && fileName == "bash" && Path.GetFileName(arguments[0]) == "terraform-exact-plan.sh")
                {
                    ProcessCall request = new(fileName, arguments, workingDirectory, environment);
                    string metadataPath = request.Option("--metadata-out")!;
                    JsonNode metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken))!;
                    metadata["action"] = "destroy";
                    metadata["actor_id"] = request.Option("--actor");
                    metadata["expires_at_utc"] = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O");
                    metadata["state_before"]!["serial"] = 12 + Mutations;
                    string variable = request.Option("--var-file")!;
                    ProvisioningProcessResult inputs = await SystemProvisioningProcessRunner.Instance.RunAsync("bash",
                        ["-c", "source \"$1\"; input_digest_doc \"$2\" \"$3\"", "owned-input-control",
                            Path.Combine(workingDirectory, "scripts", "lib", "tf-exec-contract.sh"), request.Option("--root")!, variable],
                        workingDirectory, TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
                    Assert.Equal(0, inputs.ExitCode);
                    metadata["inputs"] = JsonNode.Parse(inputs.StandardOutput)!;
                    if (Mutations > 0) RecoveryMutation?.Invoke(metadata);
                    metadata.AsObject().Remove("plan_metadata_digest");
                    await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), cancellationToken);
                    ProvisioningProcessResult digest = await SystemProvisioningProcessRunner.Instance.RunAsync("bash",
                        ["-c", "source \"$1\"; json_digest < \"$2\"", "owned-metadata-control",
                            Path.Combine(workingDirectory, "scripts", "lib", "tf-exec-contract.sh"), metadataPath],
                        workingDirectory, TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
                    Assert.Equal(0, digest.ExitCode);
                    Assert.Equal(64, digest.StandardOutput.Trim().Length);
                    metadata["plan_metadata_digest"] = digest.StandardOutput.Trim();
                    await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), cancellationToken);
                }
                return planned;
            }
            ProcessCall call = new(fileName, arguments, workingDirectory, environment);
            if (arguments.Contains("--claim-status")) StatusAfterPlanLoss |= !File.Exists(call.Option("--plan"));
            else
            {
                Mutations++;
                PlanPath = call.Option("--plan")!;
                ClaimDirectory = call.Option("--claim-dir")!;
                SawPrewrittenObligation = File.Exists(Path.Combine(Path.GetDirectoryName(ClaimDirectory)!, "obligation.json"));
                JsonNode receipt = JsonNode.Parse(ProvisioningSubstrateFixtures.ExecReceiptJson)!;
                JsonNode metadata = JsonNode.Parse(await File.ReadAllTextAsync(
                    PlanPath + ".metadata.json", cancellationToken))!;
                receipt["cleanup"]!["teardown_root"] = metadata["source"]!["terraform_root"]!.DeepClone();
                if (_destroyRecovery)
                {
                    receipt["action"] = "destroy";
                    receipt["actor_id"] = metadata["actor_id"]!.DeepClone();
                    receipt["plan_metadata_digest"] = metadata["plan_metadata_digest"]!.DeepClone();
                    receipt["approved_digest"] = metadata["plan_metadata_digest"]!.DeepClone();
                    receipt["state_before"] = metadata["state_before"]!.DeepClone();
                    receipt["state_after"]!["serial"] = 12 + Mutations;
                    if (Mutations >= SuccessAfterMutation) await File.WriteAllTextAsync(PlanPath + ".success", "owned normal exit", cancellationToken);
                }
                receipt["exit_status"] = _destroyRecovery && Mutations >= SuccessAfterMutation ? 0 : 1;
                receipt["status"] = _destroyRecovery && Mutations >= SuccessAfterMutation ? "succeeded" : "failed";
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
