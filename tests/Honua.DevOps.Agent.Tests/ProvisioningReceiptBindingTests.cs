using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class ProvisioningReceiptBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destroy_ConsumesItsApprovedPlanOnceAndRetainsPostActionReview(bool substituteAction)
    {
        using TerraformTestRoot root = new();
        JsonNode metadata = JsonNode.Parse(ProvisioningSubstrateFixtures.ExactPlanMetadataJson)!;
        metadata["action"] = "destroy";
        JsonNode receipt = JsonNode.Parse(ProvisioningSubstrateFixtures.ExecReceiptJson)!;
        receipt["action"] = substituteAction ? "apply" : "destroy";
        receipt["output_contract"]!["digest"] = null;
        FakeSubstrateRunner runner = new()
        {
            ExactPlanMetadataJson = metadata.ToJsonString(),
            ExecReceiptJson = receipt.ToJsonString(),
            PlanSummary = "Plan: 0 to add, 0 to change, 7 to destroy."
        };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(
            root.Path, ExecutionMode.Execute, ExecutionTier.BreakGlass);
        HonuaOperationsToolkit toolkit = new(runtime, gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "destroy", "{}", false, "");
        Assert.Equal("terraform-destroy-plan-ready", plan.Status);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "destroy");
        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "destroy", "{}", true, challenge, approval);

        Assert.Equal(substituteAction ? "exec-receipt-mismatch" : "infrastructure-destroyed", response.Status);
        Assert.True(Assert.Single(response.BackendSteps!, step => step.Name == "terraform-exact-destroy").MutatesState);
        if (substituteAction)
        {
            Assert.Null(response.ProvisioningLineage);
            Assert.Contains("action", response.Summary, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(plan.ProvisioningLineage!.ProvisioningOperationId, response.ProvisioningLineage!.ProvisioningOperationId);
            Assert.Equal(plan.ProvisioningLineage.PlanSha256, response.ProvisioningLineage.PlanSha256);
            Assert.Contains(response.Actions, action => action.Contains("post-action review", StringComparison.Ordinal));
        }

        HonuaOperationsToolkit restarted = new(runtime, gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse retry = await restarted.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "destroy", "{}", true, challenge, approval);
        Assert.Equal(substituteAction ? "confirmation-required" : "infrastructure-destroyed", retry.Status);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        ProcessCall applied = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
        Assert.Equal("destroy", applied.Option("--action"));
        Assert.Equal(Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh").Option("--plan-out"),
            applied.Option("--plan"));
        Assert.DoesNotContain(runner.Calls, call => call.Operation == "output");
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("rejected")]
    [InlineData("unknown-issuer")]
    [InlineData("invalid-signature")]
    [InlineData("plan-drift")]
    public async Task Apply_OnlyStartsForAnIndependentlySignedValidApprovalAndUnchangedPlan(string scenario)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        ProcessCall planCall = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        string planPath = planCall.Option("--plan-out")!;
        string expectedPlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fake saved terraform plan"))).ToLowerInvariant();
        Assert.Equal(expectedPlanHash, plan.ProvisioningLineage!.PlanSha256);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        JsonNode approval = JsonNode.Parse(ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"))!;
        DateTimeOffset issued = DateTimeOffset.UtcNow.AddMinutes(-1);
        DateTimeOffset expires = issued.AddMinutes(15);
        switch (scenario)
        {
            case "expired": issued = issued.AddHours(-1); expires = issued.AddMinutes(15); break;
            case "future": issued = issued.AddHours(1); expires = issued.AddMinutes(15); break;
            case "rejected": approval["decision"] = "rejected"; break;
            case "unknown-issuer": approval["issuer"] = "test://unknown"; break;
            case "plan-drift": await File.AppendAllTextAsync(planPath, "changed after review"); break;
        }
        approval["issuedAtUtc"] = issued.ToString("O");
        approval["expiresAtUtc"] = expires.ToString("O");
        // Independent signer: use the published field order, not the production
        // canonicalization helper or signature-provider implementation.
        string[] fields = ["schemaVersion", "approvalReceiptId", "issuer", "keyId", "provisioningOperationId",
            "planSha256", "planMetadataDigest", "action", "stack", "environment", "decision",
            "issuedAtUtc", "expiresAtUtc", "signingMode"];
        string payload = string.Join('\n', fields.Select(field => approval[field]!.GetValue<string>()));
        approval["signature"] = Convert.ToBase64String(HMACSHA256.HashData(
            ProvisioningSubstrateFixtures.ApprovalKey, Encoding.UTF8.GetBytes(payload)));
        if (scenario == "invalid-signature") approval["signature"] = Convert.ToBase64String(new byte[32]);
        HonuaOperationsToolkit executor = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        int callsBefore = runner.Calls.Count;
        try
        {
            OperationResponse response = await executor.ProvisionInfrastructureAsync(
                "aws-ecs", "small", "apply", "{}", true, challenge, approval.ToJsonString());
            if (scenario == "valid")
            {
                Assert.Equal("infrastructure-provisioned", response.Status);
                Assert.Equal(1, runner.ApplyCalls);
                Assert.Equal(expectedPlanHash, response.ProvisioningLineage!.PlanSha256);
            }
            else
            {
                Assert.Equal("confirmation-required", response.Status);
                Assert.Equal(callsBefore, runner.Calls.Count);
                Assert.Equal(0, runner.ApplyCalls);
                Assert.Null(response.ProvisioningLineage);
            }
        }
        finally
        {
            string directory = Path.GetDirectoryName(planPath)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // These are single-field substitutions in captured honua-iac wrapper output,
    // not snapshots of DevOps output. Each document remains schema-valid and echoes
    // the correct metadata digest, but disagrees with an independently stored plan.
    [Theory]
    [InlineData("approved_digest", null)]
    [InlineData("approved_digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("saved_plan_sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("action", "destroy")]
    [InlineData("actor_id", "other-operator")]
    [InlineData("target_id", "aws-ecs:other")]
    [InlineData("candidate_digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("backend_step.backend_config_digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("backend_step.backend_kind", "azurerm")]
    [InlineData("backend_step.workspace", "another-cell")]
    [InlineData("backend_step.object_key", "honua/aws/other/terraform.tfstate")]
    [InlineData("backend_step.bucket_arn", "arn:aws:s3:::other-state")]
    [InlineData("backend_step.locking.kind", "none")]
    [InlineData("backend_step.locking.detail", "other.tfstate.tflock")]
    [InlineData("workload_identity.account_id", "210987654321")]
    [InlineData("workload_identity.assumed_role_arn", "arn:aws:sts::123456789012:assumed-role/other/session")]
    [InlineData("workload_identity.role_id", "AROAOTHERID")]
    [InlineData("workload_identity.partition", "aws-cn")]
    [InlineData("workload_identity.credential_kind", "long-lived-access-key")]
    [InlineData("workload_identity.issuer", "https://unknown.example.com")]
    [InlineData("workload_identity.contract_digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("state_before.lineage", "other-lineage")]
    [InlineData("state_before.serial", "11")]
    [InlineData("cleanup.teardown_root", "infrastructure/terraform/examples/other")]
    [InlineData("output_contract.digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("output_contract.output_name", "other_contract_digest")]
    public async Task Apply_RejectsSubstitutedExecutionFactsAndCannotAuthorizeHandoff(string path, string? value)
    {
        JsonNode receipt = JsonNode.Parse(ProvisioningSubstrateFixtures.ExecReceiptJson)!;
        string[] segments = path.Split('.');
        JsonNode parent = receipt;
        foreach (string segment in segments[..^1]) parent = parent[segment]!;
        parent[segments[^1]] = path == "state_before.serial" ? JsonValue.Create(int.Parse(value!)) : JsonValue.Create(value);
        Assert.True(TerraformExecReceipt.TryRead(receipt.ToJsonString(),
            ProvisioningSubstrateFixtures.ExecReceiptSchemaJson, out _, out string schemaError), schemaError);

        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new() { ExecReceiptJson = receipt.ToJsonString() };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        HonuaOperationsToolkit executor = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);

        OperationResponse response = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{}", true, challenge, approval);

        Assert.Equal(path == "output_contract.digest" ? "operator-contract-receipt-mismatch" : "exec-receipt-mismatch", response.Status);
        if (path != "output_contract.digest") Assert.Contains(path, response.Summary, StringComparison.Ordinal);
        Assert.Null(response.ProvisioningLineage);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.True(Assert.Single(response.BackendSteps!, step => step.Name == "terraform-exact-apply").MutatesState);

        // A fresh toolkit must not recover success from durable state after the
        // evidence failure, nor may retry reuse the spent plan/approval pair.
        HonuaOperationsToolkit restarted = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        string handoffDirectory = Path.Combine(root.Path, "rejected-handoff");
        OperationResponse handoff = await restarted.InstallHandoffAsync(
            "aws-ecs", "", "", handoffDirectory, false, plan.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal("provisioning-evidence-missing", handoff.Status);
        Assert.False(Directory.Exists(handoffDirectory));
        OperationResponse retry = await restarted.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{}", true, challenge, approval);
        Assert.Equal("confirmation-required", retry.Status);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
    }
}
