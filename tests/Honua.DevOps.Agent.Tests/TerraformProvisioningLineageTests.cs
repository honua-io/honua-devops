using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Fact]
    public async Task Lineage_KeyedPlanRestartAndConcurrentDeliveryPreserveIdentityAndRejectDifferentRequest()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        HonuaOperationsToolkit planner = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: runner);
        string key = Guid.NewGuid().ToString("n");
        OperationResponse[] plans = await Task.WhenAll(
            planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "", idempotencyKey: key),
            planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "", idempotencyKey: key));
        Assert.All(plans, plan => Assert.Equal("terraform-plan-ready", plan.Status));
        Assert.Equal(plans[0].ProvisioningLineage!.ProvisioningOperationId, plans[1].ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(plans[0].ProvisioningLineage!.PlanSha256, plans[1].ProvisioningLineage!.PlanSha256);
        Assert.NotEqual(plans[0].AuditEventId, plans[1].AuditEventId);
        FakeSubstrateRunner restartedRunner = new();
        HonuaOperationsToolkit restarted = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: restartedRunner);
        OperationResponse replay = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "", idempotencyKey: key);
        Assert.Equal(plans[0].ProvisioningLineage!.ProvisioningOperationId, replay.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Empty(restartedRunner.Calls);
        OperationResponse conflict = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{\"environment\":\"staging\"}", false, "", idempotencyKey: key);
        Assert.Equal("idempotency-conflict", conflict.Status);
        Assert.Empty(restartedRunner.Calls);
        DeleteSavedPlanFrom(Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh"));
    }

    [Fact]
    public async Task Lineage_PlanCrashKeepsReservedIdentityAndNeverReplansOnRetry()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new() { BeforePlan = () => throw new IOException("injected process loss") };
        HonuaOperationsToolkit planner = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: runner);
        string key = Guid.NewGuid().ToString("n");
        await Assert.ThrowsAsync<IOException>(() => planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "", idempotencyKey: key));
        string actor = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh").Option("--actor")!;
        runner.BeforePlan = null;
        HonuaOperationsToolkit restarted = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: runner);
        OperationResponse replay = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "", idempotencyKey: key);
        Assert.Equal("plan-indeterminate", replay.Status);
        Assert.Equal(actor["honua-devops:".Length..], replay.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
    }

    [Fact]
    public async Task Lineage_ExactEvidenceSurvivesPlanCleanupWireSerializationAndAudit()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        (_, OperationResponse apply) = await ApplyAsync(root, runner, gateway);
        string wire = JsonSerializer.Serialize(apply);
        OperationResponse decoded = JsonSerializer.Deserialize<OperationResponse>(wire)!;
        ProvisioningLineage lineage = decoded.ProvisioningLineage!;
        ProvisioningEvidenceStore store = RetainedEvidence();
        Assert.Equal(apply.AuditEventId, decoded.AuditEventId);
        Assert.Equal(lineage.ApplyAuditEventId, decoded.AuditEventId);
        Assert.DoesNotContain("\"operationId\"", wire, StringComparison.Ordinal);
        Assert.Null(lineage.ServerOperationId);
        Assert.Null(lineage.ServerProposalId);
        Assert.Null(lineage.ReleaseReceiptReference);

        // Independently specified inputs, not a snapshot of the implementation output.
        byte[] planBytes = Encoding.UTF8.GetBytes("fake saved terraform plan");
        ProvisioningEvidenceReference plan = Assert.Single(lineage.EvidenceRefs!, r => r.Kind == "plan");
        Assert.Equal(planBytes, store.Read(plan));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(planBytes)), lineage.PlanSha256);
        // Trunk #189 deliberately retains the claimed plan through the audit
        // acknowledgement window so a restarted host can reconcile it safely.
        Assert.True(File.Exists(Assert.Single(runner.Calls, c => c.Operation == "terraform-exact-plan.sh").Option("--plan-out")));
        ProvisioningEvidenceReference execution = Assert.Single(lineage.EvidenceRefs!, r => r.Kind == "apply");
        Assert.Equal(Encoding.UTF8.GetBytes(ProvisioningSubstrateFixtures.ExecReceiptJson), store.Read(execution));
        Assert.Equal(execution.Reference, lineage.ActuatorReceiptReference);
        ProvisioningEvidenceReference approval = Assert.Single(lineage.EvidenceRefs!, r => r.Kind == "approval");
        Assert.Equal(approval.Sha256, lineage.ApprovalReceiptSha256);
        using JsonDocument approvalDocument = JsonDocument.Parse(store.Read(approval));
        Assert.Equal(lineage.ProvisioningOperationId, approvalDocument.RootElement.GetProperty("provisioningOperationId").GetString());

        string auditPath = Path.Combine(root.Path, "audit.jsonl");
        await using (JsonlAuditSink sink = JsonlAuditSink.ForFile(auditPath))
        {
            // Exercise the serialized MCP/CLI path, not just in-process object access.
            await ToolCallAuditor.EmitAsync(new("session", "execute", "execute-lower-env", "direct-allowed", "mcp", sink),
                new("provision_infrastructure", null), wire, CancellationToken.None);
        }
        using JsonDocument audit = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
        Assert.Equal(apply.AuditEventId, audit.RootElement.GetProperty("auditEventId").GetString());
        Assert.Equal(lineage.ProvisioningOperationId, audit.RootElement.GetProperty("ProvisioningLineage").GetProperty("provisioningOperationId").GetString());
        Assert.Equal(4, audit.RootElement.GetProperty("ProvisioningLineage").GetProperty("evidenceRefs").GetArrayLength());
        File.Delete(auditPath);
        store.Validate(lineage.EvidenceRefs!); // JSONL was only a diagnostic replica.
    }

    [Fact]
    public async Task Lineage_DuplicateHandoffAndRestartReuseExactReceiptWithoutProbingAgain()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(true);
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway, verifier);
        string directory = Path.Combine(root.Path, "lineage-handoff");
        string config = Path.Combine(directory, "honua-mcp-proxy.handoff.json");
        OperationResponse handoff = await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, apply.ProvisioningLineage!.ProvisioningOperationId);
        byte[] originalHandoff = await File.ReadAllBytesAsync(config);
        OperationResponse duplicate = await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, apply.ProvisioningLineage.ProvisioningOperationId);
        Assert.Equal("install-handoff-written", duplicate.Status);
        Assert.Equal(originalHandoff, await File.ReadAllBytesAsync(config));
        Assert.Equal(handoff.ProvisioningLineage!.HandoffReceiptSha256, duplicate.ProvisioningLineage!.HandoffReceiptSha256);
        OperationResponse[] verified = await Task.WhenAll(toolkit.VerifyInstallHandoffAsync(config, false), toolkit.VerifyInstallHandoffAsync(config, false));
        Assert.All(verified, result => Assert.Equal("install-handoff-verified", result.Status));
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(verified[0].ProvisioningLineage!.HandoffVerificationReceiptId, verified[1].ProvisioningLineage!.HandoffVerificationReceiptId);
        string receipt = Path.Combine(directory, "honua-install-verification.receipt.json");
        string binding = Path.Combine(directory, "honua-devops-aws-ecs-provision-binding.json");
        byte[] receiptBytes = await File.ReadAllBytesAsync(receipt);
        byte[] bindingBytes = await File.ReadAllBytesAsync(binding);
        File.Delete(receipt);
        File.Delete(binding);
        FakeInstallHandoffVerifier unavailable = new(false);
        HonuaOperationsToolkit restarted = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, installHandoffVerifier: unavailable);
        OperationResponse recovered = await restarted.VerifyInstallHandoffAsync(config, false);
        Assert.Equal("install-handoff-verified", recovered.Status);
        Assert.Equal(0, unavailable.Calls);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receipt));
        Assert.Equal(bindingBytes, await File.ReadAllBytesAsync(binding));
        Assert.Equal(apply.ProvisioningLineage.ProvisioningOperationId, recovered.ProvisioningLineage!.RootProvisioningOperationId);
        ProvisioningEvidenceReference evidence = Assert.Single(recovered.ProvisioningLineage.EvidenceRefs!, r => r.Kind == "verification-evidence");
        Assert.Equal(evidence.Sha256, recovered.ProvisioningLineage.HandoffVerificationReceiptSha256);
        Assert.Equal(evidence.Reference, recovered.ProvisioningLineage.HandoffVerificationReceiptId);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("candidateReference")]
    [InlineData("provisioningLineage")]
    public async Task Lineage_SubstitutedHandoffNeverStartsVerifier(string field)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(true);
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway, verifier);
        string directory = Path.Combine(root.Path, "substitution");
        await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, apply.ProvisioningLineage!.ProvisioningOperationId);
        string config = Path.Combine(directory, "honua-mcp-proxy.handoff.json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(config))!;
        document[field] = field == "provisioningLineage" ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonValue.Create("substituted");
        await File.WriteAllTextAsync(config, document.ToJsonString());
        OperationResponse result = await toolkit.VerifyInstallHandoffAsync(config, true);
        Assert.Contains(result.Status, new[] { "handoff-evidence-mismatch", "handoff-pin-mismatch" });
        Assert.Equal(0, verifier.Calls);
        Assert.False(File.Exists(Path.Combine(directory, "honua-devops-aws-ecs-provision-binding.json")));
    }

    [Fact]
    public async Task Lineage_ExportFailureRecoversPersistedReceiptWithoutSecondVerification()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(true);
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway, verifier);
        string directory = Path.Combine(root.Path, "export-failure");
        await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, apply.ProvisioningLineage!.ProvisioningOperationId);
        string config = Path.Combine(directory, "honua-mcp-proxy.handoff.json");
        string binding = Path.Combine(directory, "honua-devops-aws-ecs-provision-binding.json");
        Directory.CreateDirectory(binding); // Real filesystem fault on the second export.
        await Assert.ThrowsAnyAsync<IOException>(() => toolkit.VerifyInstallHandoffAsync(config, true));
        byte[] receipt = await File.ReadAllBytesAsync(Path.Combine(directory, "honua-install-verification.receipt.json"));
        Directory.Delete(binding);
        OperationResponse recovered = await toolkit.VerifyInstallHandoffAsync(config, false);
        Assert.Equal("install-handoff-verified", recovered.Status);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(receipt, await File.ReadAllBytesAsync(Path.Combine(directory, "honua-install-verification.receipt.json")));
        Assert.True(File.Exists(binding));
    }

    private static ProvisioningEvidenceStore RetainedEvidence() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "honua-devops", "provisioning", "evidence"));
}
