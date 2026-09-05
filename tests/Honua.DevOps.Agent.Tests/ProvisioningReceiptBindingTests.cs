using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class ProvisioningReceiptBindingTests
{
    // These are single-field substitutions in captured honua-iac wrapper output,
    // not snapshots of DevOps output. Each document remains schema-valid and echoes
    // the correct metadata digest, but disagrees with an independently stored plan.
    [Theory]
    [InlineData("approved_digest", null)]
    [InlineData("approved_digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("saved_plan_sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("action", "destroy")]
    [InlineData("backend_step.backend_config_digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("backend_step.backend_kind", "azurerm")]
    [InlineData("backend_step.workspace", "another-cell")]
    [InlineData("backend_step.object_key", "honua/aws/other/terraform.tfstate")]
    [InlineData("workload_identity.account_id", "210987654321")]
    [InlineData("workload_identity.assumed_role_arn", "arn:aws:sts::123456789012:assumed-role/other/session")]
    [InlineData("workload_identity.role_id", "AROAOTHERID")]
    [InlineData("workload_identity.partition", "aws-cn")]
    [InlineData("workload_identity.credential_kind", "long-lived-access-key")]
    [InlineData("state_before.lineage", "other-lineage")]
    [InlineData("state_before.serial", "11")]
    [InlineData("cleanup.teardown_root", "infrastructure/terraform/examples/other")]
    [InlineData("output_contract.digest", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Apply_RejectsSubstitutedExecutionFactsAndCannotAuthorizeHandoff(string path, string? value)
    {
        JsonNode receipt = JsonNode.Parse(ProvisioningSubstrateFixtures.ExecReceiptJson)!;
        string[] segments = path.Split('.');
        JsonNode parent = segments.Length == 1 ? receipt : receipt[segments[0]]!;
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
