using System.Net;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class InstallHonuaTests
{
    private const string Variables = """{"environment":"dev","region":"us-east-1","honua_image":"ghcr.io/honua-io/honua-server:v2026.1"}""";

    private static HonuaOperationsToolkit Toolkit(TerraformTestRoot root, BackendGateway gateway,
        FakeSubstrateRunner runner, bool execute = false, IInstallHandoffVerifier? verifier = null)
        => new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path,
                execute ? ExecutionMode.Execute : ExecutionMode.Plan,
                execute ? ExecutionTier.ExecuteLowerEnv : ExecutionTier.Plan),
            gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner, installHandoffVerifier: verifier);

    [Fact]
    public async Task FullSequenceSurvivesRestartsAndOnlyCompletesWithVerifiedHandoff()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        List<string> requests = [];
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway(new TestHttpMessageHandler(request =>
        {
            lock (requests) requests.Add(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }));
        string output = Path.Combine(root.Path, "guided-handoff");
        OperationResponse intake = await Toolkit(root, gateway, runner).InstallHonuaAsync();
        Assert.Equal("install-input-required", intake.Status);
        Assert.Contains("requirements", intake.Installation!.NextQuestion);
        string id = intake.Installation.InstallationId;
        Assert.Empty(runner.Calls);

        OperationResponse target = await Toolkit(root, gateway, runner).InstallHonuaAsync(id, "Small public GIS portal, budget sensitive");
        Assert.Contains("target", target.Installation!.NextQuestion);
        OperationResponse inputs = await Toolkit(root, gateway, runner).InstallHonuaAsync(id, stack: "aws-ecs");
        Assert.Contains("environment", inputs.Installation!.NextQuestion);
        Assert.Empty(runner.Calls);
        OperationResponse region = await Toolkit(root, gateway, runner).InstallHonuaAsync(id, variablesJson: """{"environment":"dev"}""");
        Assert.Contains("region", region.Installation!.NextQuestion);
        OperationResponse image = await Toolkit(root, gateway, runner).InstallHonuaAsync(id, variablesJson: """{"region":"us-east-1"}""");
        Assert.Contains("honua_image", image.Installation!.NextQuestion);

        // Premature confirmation does not cause apply on the plan-producing call.
        OperationResponse plan = await Toolkit(root, gateway, runner, execute: true).InstallHonuaAsync(id,
            variablesJson: """{"honua_image":"ghcr.io/honua-io/honua-server:v2026.1"}""", outputDirectory: output, confirmed: true, confirmation: "unreviewed", approvalReceiptJson: "{}");
        Assert.Equal("terraform-plan-ready", plan.Status);
        Assert.Equal("approval", plan.Installation!.Stage);
        Assert.NotNull(plan.ApprovalRequest);
        Assert.Contains(plan.Findings, f => f.Contains("account 123456789012", StringComparison.Ordinal));
        Assert.Contains(plan.Findings, f => f.Contains("Install region: us-east-1", StringComparison.Ordinal));
        Assert.Contains(plan.Findings, f => f.Contains("aws_ecs_service.honua", StringComparison.Ordinal));
        Assert.Contains(plan.Findings, f => f.Contains("Cost drivers:", StringComparison.Ordinal));
        Assert.Contains(plan.Findings, f => f.Contains(plan.ApprovalRequest.PlanMetadataDigest, StringComparison.Ordinal));
        Assert.Contains("Present account", plan.Installation.NextStep);
        Assert.Contains(requests, p => p.EndsWith("/manifest", StringComparison.Ordinal));
        Assert.Contains(requests, p => p.EndsWith("/metrics/performance", StringComparison.Ordinal));
        Assert.Contains(requests, p => p.EndsWith("/metrics/cache", StringComparison.Ordinal));
        Assert.Equal(0, runner.ApplyCalls);

        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        OperationResponse applied = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id,
            confirmed: true, confirmation: challenge, approvalReceiptJson: approval);
        Assert.Equal("infrastructure-provisioned", applied.Status);
        Assert.Equal("handoff", applied.Installation!.Stage);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.False(Directory.Exists(output));

        OperationResponse handoff = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id);
        Assert.Equal("install-handoff-written", handoff.Status);
        Assert.Equal("verify", handoff.Installation!.Stage);
        Assert.False(File.Exists(Path.Combine(output, "honua-install-verification.receipt.json")));

        FakeInstallHandoffVerifier verifier = new(true);
        OperationResponse verified = await Toolkit(root, gateway, runner, true, verifier).InstallHonuaAsync(id);
        Assert.Equal("install-handoff-verified", verified.Status);
        Assert.Equal("complete", verified.Installation!.Stage);
        Assert.Contains("https://honua.example.com/mcp", verified.Installation.NextStep);
        Assert.Contains("day-2", verified.Installation.NextStep);
        Assert.NotNull(verified.ProvisioningLineage!.HandoffVerificationReceiptId);
        Assert.True(File.Exists(Path.Combine(output, "honua-install-verification.receipt.json")));
        Assert.True(File.Exists(Path.Combine(output, "honua-devops-aws-ecs-provision-binding.json")));
        Assert.Equal(1, verifier.Calls);

        OperationResponse replay = await Toolkit(root, gateway, runner, true, verifier).InstallHonuaAsync(id);
        Assert.Equal("complete", replay.Installation!.Stage);
        Assert.Equal(verified.ProvisioningLineage.HandoffVerificationReceiptId, replay.ProvisioningLineage!.HandoffVerificationReceiptId);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.Single(runner.Calls, c => c.Operation == "terraform-exact-plan.sh");
        Assert.Equal(1, verifier.Calls);
    }

    [Theory]
    [InlineData("aws-lambda", "database initialization")]
    [InlineData("aws-eks", "qualification")]
    [InlineData("azure-functions", "aws-ecs/small")]
    public async Task UnqualifiedFirstInstallTargetNeverStartsTerraform(string stack, string reason)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        OperationResponse result = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(
            customerRequirements: "Install Honua", stack: stack, variablesJson: Variables,
            confirmed: true, confirmation: "anything", approvalReceiptJson: "{}");
        Assert.Equal("install-target-unqualified", result.Status);
        Assert.Equal("blocked", result.Installation!.Stage);
        Assert.Contains(reason, result.Summary);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-digest")]
    [InlineData("valid")]
    public async Task ApplyAlwaysUsesExistingApprovedDigestAndExecutionGates(string receiptKind)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        OperationResponse plan = await Toolkit(root, gateway, runner).InstallHonuaAsync(
            customerRequirements: "GIS portal", stack: "aws-ecs", variablesJson: Variables);
        Assert.Equal("terraform-plan-ready", plan.Status);
        string id = plan.Installation!.InstallationId;
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string receipt = receiptKind == "missing" ? "" : ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply",
            overridePlanMetadataDigest: receiptKind == "wrong-digest" ? new string('a', 64) : null);
        OperationResponse unconfirmed = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id,
            confirmed: false, confirmation: challenge, approvalReceiptJson: receipt);
        Assert.Equal(0, runner.ApplyCalls);
        Assert.Equal("approval", unconfirmed.Installation!.Stage);
        // Even a correctly signed receipt cannot elevate a plan-mode host.
        OperationResponse gated = await Toolkit(root, gateway, runner).InstallHonuaAsync(id,
            confirmed: true, confirmation: challenge, approvalReceiptJson: receipt);
        Assert.NotEqual("infrastructure-provisioned", gated.Status);
        Assert.Equal(0, runner.ApplyCalls);

        OperationResponse result = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id,
            confirmed: true, confirmation: challenge, approvalReceiptJson: receipt);
        Assert.Equal(receiptKind == "valid" ? "infrastructure-provisioned" : "confirmation-required", result.Status);
        Assert.Equal(receiptKind == "valid" ? 1 : 0, runner.ApplyCalls);
        if (receiptKind == "valid")
        {
            ProcessCall apply = Assert.Single(runner.Calls, c => c.Operation == "terraform-exact-apply.sh");
            Assert.Equal(plan.ApprovalRequest!.PlanMetadataDigest, apply.Option("--approved-digest"));
            Assert.Equal("1", apply.Environment!["HONUA_IAC_REQUIRE_APPROVAL"]);
        }
    }

    [Fact]
    public async Task FailedVerificationNeverCompletesAndCanResumeWithoutReapplying()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        OperationResponse plan = await Toolkit(root, gateway, runner).InstallHonuaAsync(
            customerRequirements: "GIS portal", stack: "aws-ecs", variablesJson: Variables, outputDirectory: Path.Combine(root.Path, "handoff"));
        string id = plan.Installation!.InstallationId;
        await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id, confirmed: true,
            confirmation: ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="),
            approvalReceiptJson: ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"));
        await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id);
        OperationResponse failed = await Toolkit(root, gateway, runner, true, new FakeInstallHandoffVerifier(false)).InstallHonuaAsync(id);
        Assert.Equal("verify", failed.Installation!.Stage);
        Assert.NotEqual("install-handoff-verified", failed.Status);
        Assert.False(File.Exists(Path.Combine(root.Path, "handoff", "honua-install-verification.receipt.json")));
        OperationResponse recovered = await Toolkit(root, gateway, runner, true, new FakeInstallHandoffVerifier(true)).InstallHonuaAsync(id);
        Assert.Equal("complete", recovered.Installation!.Stage);
        Assert.Equal(1, runner.ApplyCalls);
    }

    [Fact]
    public async Task PlanInputsCannotChangeAndSecretVariablesAreNeverPersisted()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        string id = Guid.NewGuid().ToString("n");
        OperationResponse refused = await Toolkit(root, gateway, runner).InstallHonuaAsync(id,
            customerRequirements: "GIS portal", stack: "aws-ecs", variablesJson: """{"db_password":"never-persist-me"}""");
        Assert.Equal("variables-invalid", refused.Status);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain("never-persist-me", JsonSerializer.Serialize(refused));

        OperationResponse plan = await Toolkit(root, gateway, runner).InstallHonuaAsync(id,
            customerRequirements: "GIS portal", stack: "aws-ecs", variablesJson: Variables);
        OperationResponse changed = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(id,
            variablesJson: Variables.Replace("us-east-1", "us-west-2", StringComparison.Ordinal));
        Assert.Equal("installation-input-conflict", changed.Status);
        Assert.Equal(0, runner.ApplyCalls);
        OperationResponse review = await Toolkit(root, gateway, runner).InstallHonuaAsync(id);
        Assert.Equal(plan.ApprovalRequest, review.ApprovalRequest);
        Assert.Single(runner.Calls, c => c.Operation == "terraform-exact-plan.sh");
    }

    [Fact]
    public async Task FirstInstallWithoutAnExistingServerRetainsAdvisoryFailuresAndStopsAtPlanReview()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway(new TestHttpMessageHandler(
            _ => new(HttpStatusCode.ServiceUnavailable)));
        FakeSubstrateRunner runner = new();
        OperationResponse plan = await Toolkit(root, gateway, runner).InstallHonuaAsync(
            customerRequirements: "New GIS portal", stack: "aws-ecs", variablesJson: Variables);
        Assert.Equal("terraform-plan-ready", plan.Status);
        Assert.Equal("approval", plan.Installation!.Stage);
        Assert.Contains(plan.Risks, r => r.Contains("Pre-install advisory unavailable", StringComparison.Ordinal));
        Assert.Equal(0, runner.ApplyCalls);
        OperationResponse resumed = await Toolkit(root, gateway, runner).InstallHonuaAsync(plan.Installation.InstallationId);
        Assert.Equal(plan.Risks, resumed.Risks);
    }

    [Fact]
    public async Task AnotherInstallationsValidPlanAndReceiptCannotReplaceTheReviewedPlan()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        OperationResponse first = await Toolkit(root, gateway, runner).InstallHonuaAsync(
            customerRequirements: "GIS portal", stack: "aws-ecs", variablesJson: Variables);
        OperationResponse second = await Toolkit(root, gateway, runner).InstallHonuaAsync(
            customerRequirements: "GIS portal", stack: "aws-ecs", variablesJson: Variables);
        Assert.NotEqual(first.ConfirmationChallenge, second.ConfirmationChallenge);
        Assert.NotEqual(first.ProvisioningLineage!.ProvisioningOperationId, second.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(ProvisioningSubstrateFixtures.ExtractChallenge(first, "confirmation="), first.ConfirmationChallenge);
        string secondApproval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(second, "apply");
        OperationResponse substituted = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(first.Installation!.InstallationId,
            confirmed: true, confirmation: second.ConfirmationChallenge!, approvalReceiptJson: secondApproval);
        Assert.Equal("installation-plan-mismatch", substituted.Status);
        Assert.Equal("approval", substituted.Installation!.Stage);
        Assert.Equal(0, runner.ApplyCalls);

        // The original challenge plus another plan's signed receipt is refused by
        // the existing approval gate, without duplicating receipt policy here.
        OperationResponse wrongReceipt = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(first.Installation.InstallationId,
            confirmed: true, confirmation: first.ConfirmationChallenge!, approvalReceiptJson: secondApproval);
        Assert.Equal("confirmation-required", wrongReceipt.Status);
        Assert.Equal(0, runner.ApplyCalls);
        OperationResponse applied = await Toolkit(root, gateway, runner, true).InstallHonuaAsync(first.Installation.InstallationId,
            confirmed: true, confirmation: first.ConfirmationChallenge!,
            approvalReceiptJson: ProvisioningSubstrateFixtures.CreateApprovalReceipt(first, "apply"));
        Assert.Equal("infrastructure-provisioned", applied.Status);
        Assert.Equal(first.ProvisioningLineage.ProvisioningOperationId, applied.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(1, runner.ApplyCalls);
    }
}
