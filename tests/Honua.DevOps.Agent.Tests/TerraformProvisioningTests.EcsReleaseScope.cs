using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Fact]
    public async Task EcsReleaseScope_ExactPlanApprovalAndIdentityEvidenceSurviveRestartThroughApprovedDestroy()
    {
        using TerraformTestRoot root = new();
        TerraformTestRoot.WriteDeployIdentityContract(root.Path, redis: false, customDomain: true);
        const string variables = """{"redis_enabled":false,"domain_name":"gis.example.com","route53_zone_id":"Z123ABC"}""";
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit Create(ExecutionTier tier) => new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, tier), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse plan = await Create(ExecutionTier.ExecuteLowerEnv).ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", variables, false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        ProcessCall planned = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        Assert.Equal(Path.Combine(root.Path, "infrastructure", "terraform", "examples", "aws"), planned.Option("--root"));
        byte[] reviewedPlan = File.ReadAllBytes(planned.Option("--plan-out")!);
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        OperationResponse applied = await Create(ExecutionTier.ExecuteLowerEnv).ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", variables, true, plan.ConfirmationChallenge!, approval);
        Assert.Equal("infrastructure-provisioned", applied.Status);
        ProcessCall mutation = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
        Assert.Equal(planned.Option("--plan-out"), mutation.Option("--plan"));
        Assert.Equal(plan.ApprovalRequest!.PlanMetadataDigest, mutation.Option("--approved-digest"));
        Assert.Equal("1", mutation.Environment![TerraformExactSubstrate.RequireApprovalVariable]);
        Assert.Null(mutation.Option("--var-file"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(reviewedPlan)), applied.ProvisioningLineage!.PlanSha256);
        ProvisioningEvidenceStore store = RetainedEvidence();
        store.ValidateAppliedLineage(applied.ProvisioningLineage, requireHandoff: false);
        var identity = Assert.Single(applied.ProvisioningLineage.EvidenceRefs!, reference => reference.Kind == "deploy-identity-contract");
        Assert.Equal(File.ReadAllBytes(Path.Combine(root.Path, "deploy-identity-contract.json")), store.Read(identity));
        Assert.Equal(plan.DeployIdentity!.ContractSha256, identity.Sha256);
        Assert.Equal(Encoding.UTF8.GetBytes(approval), store.Read(Assert.Single(applied.ProvisioningLineage.EvidenceRefs!, reference => reference.Kind == "approval")));
        int calls = runner.Calls.Count;
        OperationResponse replay = await Create(ExecutionTier.ExecuteLowerEnv).ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", variables, true, plan.ConfirmationChallenge!, approval);
        Assert.Equal("infrastructure-provisioned", replay.Status);
        Assert.Equal(calls, runner.Calls.Count);
        Assert.Equal(JsonSerializer.Serialize(applied.ProvisioningLineage), JsonSerializer.Serialize(replay.ProvisioningLineage));

        OperationResponse destroyPlan = await Create(ExecutionTier.BreakGlass).ProvisionInfrastructureAsync(
            "aws-ecs", "small", "destroy", variables, false, "");
        Assert.Equal("terraform-destroy-plan-ready", destroyPlan.Status);
        string destroyApproval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(destroyPlan, "destroy");
        OperationResponse destroyed = await Create(ExecutionTier.BreakGlass).ProvisionInfrastructureAsync(
            "aws-ecs", "small", "destroy", variables, true, destroyPlan.ConfirmationChallenge!, destroyApproval);
        Assert.Equal("infrastructure-destroyed", destroyed.Status);
        Assert.Equal(destroyPlan.ProvisioningLineage!.PlanMetadataDigest, destroyed.ProvisioningLineage!.PlanMetadataDigest);
        Assert.Equal(2, runner.ApplyCalls);
        store.ValidateAppliedLineage(destroyed.ProvisioningLineage, requireHandoff: false);
        Assert.Contains(destroyed.Actions, action => action.Contains("review", StringComparison.OrdinalIgnoreCase));
        // Fixture process receipts establish the product contract only; no healthy
        // install, cloud deletion or live qualification is asserted here.
    }

    [Theory]
    [InlineData("aws-eks", "qualification")]
    [InlineData("aws-lambda", "initialization")]
    public async Task EcsReleaseScope_OtherFirstInstallTargetsRemainTypedUnqualifiedRefusals(string stack, string reason)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
            ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse result = await toolkit.InstallHonuaAsync(customerRequirements: "Install a public GIS portal", stack: stack,
            variablesJson: """{"environment":"dev","region":"us-east-1","honua_image":"ghcr.io/honua-io/honua-server:v2026.1"}""",
            confirmed: true, confirmation: "unreviewed", approvalReceiptJson: "{}");
        Assert.Equal("install-target-unqualified", result.Status);
        Assert.Contains(reason, result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
        Assert.Null(result.ProvisioningLineage);
    }
}
