using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class TerraformProvisioningTests
{
    [Fact]
    public async Task Plan_RoutesThroughTheExactPlanWrapperAndNeverStartsApply()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{\"region\":\"us-west-2\",\"honua_image\":\"ghcr.io/honua-io/honua-server:v2026.1\"}",
            confirmed: false,
            confirmation: string.Empty);

        Assert.Equal("terraform-plan-ready", response.Status);

        // The governed wrapper, not hand-rolled Terraform argv. In particular there is
        // no bare `terraform init`: init and backend resolution belong to the substrate.
        ProcessCall plan = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        Assert.Equal("bash", plan.FileName);
        Assert.Equal(root.PlanScript, plan.Arguments[0]);
        Assert.Equal(root.AwsRoot, plan.Option("--root"));
        Assert.Equal("apply", plan.Option("--action"));
        Assert.NotNull(plan.Option("--plan-out"));
        Assert.NotNull(plan.Option("--metadata-out"));
        Assert.NotNull(plan.Option("--var-file"));
        Assert.Equal("aws-ecs:dev", plan.Option("--target-id"));
        Assert.StartsWith("honua-devops:urn:honua:provisioning:", plan.Option("--actor")!, StringComparison.Ordinal);

        Assert.DoesNotContain(runner.Calls, call => call.Operation is "init" or "plan" or "apply");
        Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");

        // The caller must be able to review WHAT it is confirming, not just three numbers.
        Assert.Contains(response.Findings, finding => finding.Contains("aws_ecs_service.honua", StringComparison.Ordinal));
        string destructive = Assert.Single(
            response.Findings,
            finding => finding.StartsWith("DESTRUCTIVE changes in this plan:", StringComparison.Ordinal));
        Assert.Contains("must be replaced aws_db_instance.honua", destructive, StringComparison.Ordinal);
        Assert.Contains("will be destroyed aws_s3_bucket.stale", destructive, StringComparison.Ordinal);

        DeleteSavedPlanFrom(plan);
    }

    [Fact]
    public async Task Plan_SurfacesTheApprovalDigestBackendIdentityAndStateLineage()
    {
        // Everything an approver needs in order to bind an approval the substrate will
        // accept, and everything a reader needs to know which state and identity the
        // plan was computed against.
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);

        Assert.Equal("terraform-plan-ready", response.Status);
        string digest = ProvisioningSubstrateFixtures.FixturePlanMetadataDigest;
        Assert.Contains(response.Findings, finding => finding.Contains($"plan_metadata_digest: {digest}", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("backend_config_digest 0ea699e4", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("Prior state lineage 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9 serial 12", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("arn:aws:sts::123456789012:assumed-role/honua-deploy-dev/session", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("evidence mode: offline-test", StringComparison.Ordinal));

        // The lineage carries the digest an approval binds, not only the plan hash.
        Assert.Equal(digest, response.ProvisioningLineage!.PlanMetadataDigest);
        Assert.Contains(
            response.ValidationChecks,
            check => check.Contains("terraform-exact-plan.v1.schema.json", StringComparison.Ordinal));

        DeleteSavedPlanFrom(Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh"));
    }

    [Fact]
    public async Task Plan_DerivesTheDefaultNamePrefixFromTheSelectedEnvironment()
    {
        // A staging plan without an explicit name_prefix must not target `honua-dev`
        // infrastructure: that collides with (or reconciles) the existing development
        // cell, including under a break-glass destroy.
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"staging\"}", false, string.Empty);

        Assert.Equal("terraform-plan-ready", response.Status);
        ProcessCall plan = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        string variables = File.ReadAllText(plan.Option("--var-file")!);
        Assert.Contains("\"name_prefix\": \"honua-staging\"", variables, StringComparison.Ordinal);
        Assert.DoesNotContain("honua-dev", variables, StringComparison.Ordinal);
        Assert.Equal("aws-ecs:staging", plan.Option("--target-id"));
        DeleteSavedPlanFrom(plan);
    }

    [Fact]
    public async Task Plan_KeepsAnExplicitNamePrefixOverTheEnvironmentDefault()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan",
            "{\"environment\":\"staging\",\"name_prefix\":\"honua-cell-a\"}", false, string.Empty);

        Assert.Equal("terraform-plan-ready", response.Status);
        ProcessCall plan = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh");
        Assert.Contains(
            "\"name_prefix\": \"honua-cell-a\"",
            File.ReadAllText(plan.Option("--var-file")!),
            StringComparison.Ordinal);
        DeleteSavedPlanFrom(plan);
    }

    [Fact]
    public async Task Plan_RefusesWithoutStartingAnythingWhenTerraformIsNotInstalled()
    {
        // The published MCP container ships only the operator binary, so this is the
        // documented state there until Terraform and the honua-iac checkout are mounted.
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new() { TerraformAvailable = false };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("terraform-unavailable", response.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task PlanFailure_NeverStartsApply()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new() { PlanFailure = "Error: invalid provider configuration" };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        // A non-governed failure stays a plain failure; it must not masquerade as a
        // typed refusal.
        Assert.Equal("terraform-plan-failed", response.Status);
        Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
    }

    [Fact]
    public async Task Apply_ConsumesTheSavedPlanThroughTheApplyWrapperUnderApprovalEnforcement()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse planResponse = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(planResponse, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(planResponse, "apply");

        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, approval);

        Assert.Equal("infrastructure-provisioned", response.Status);
        ProcessCall apply = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");

        // The approval digest the substrate enforces, and nothing that could
        // regenerate or re-parameterize the plan.
        Assert.Equal(ProvisioningSubstrateFixtures.FixturePlanMetadataDigest, apply.Option("--approved-digest"));
        Assert.Equal("apply", apply.Option("--action"));
        Assert.NotNull(apply.Option("--receipt-out"));
        Assert.DoesNotContain(apply.Arguments, argument => argument.StartsWith("--var", StringComparison.Ordinal));
        Assert.DoesNotContain(apply.Arguments, argument => argument == "--allow-unqualified");

        // honua-devops sets approval enforcement itself rather than inheriting it.
        Assert.NotNull(apply.Environment);
        Assert.Equal("1", apply.Environment!["HONUA_IAC_REQUIRE_APPROVAL"]);

        Assert.True(Assert.Single(response.BackendSteps!, step => step.Name == "terraform-exact-apply").MutatesState);
    }

    [Fact]
    public async Task Apply_RefusesBeforeProcessStartWithoutTrustedPlanBoundApprovalReceipt()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");

        HonuaOperationsToolkit executor = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);
        OperationResponse missing = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, string.Empty);
        OperationResponse substituted = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge,
            ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "destroy"));

        Assert.Equal("confirmation-required", missing.Status);
        Assert.Contains("signed honua.devops.provision-approval/v1", missing.Summary, StringComparison.Ordinal);
        Assert.Equal("confirmation-required", substituted.Status);
        Assert.Contains("exact operation", substituted.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
    }

    [Fact]
    public async Task Apply_RefusesAnApprovalThatDoesNotBindThePlanMetadataDigest()
    {
        // The saved-plan hash alone leaves the backend, account, role, inputs and prior
        // state unapproved. An approval that binds only the old value must not pass.
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string wrongDigest = ProvisioningSubstrateFixtures.CreateApprovalReceipt(
            plan, "apply", overridePlanMetadataDigest: new string('a', 64));

        HonuaOperationsToolkit executor = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, wrongDigest);

        Assert.Equal("confirmation-required", response.Status);
        Assert.Contains("plan metadata digest", response.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
    }

    [Fact]
    public async Task Apply_AtomicallyClaimsSavedPlanAndRefusesConcurrentReuse()
    {
        using TerraformTestRoot root = new();
        TaskCompletionSource<bool> applyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowApply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSubstrateRunner runner = new();
        runner.BeforeApply = () =>
        {
            applyStarted.TrySetResult(true);
            return allowApply.Task;
        };

        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse planResponse = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(planResponse, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(planResponse, "apply");

        HonuaOperationsToolkit executor = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);
        Task<OperationResponse> firstApply = executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, approval);
        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        OperationResponse concurrentApply = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, approval);

        Assert.Equal("confirmation-required", concurrentApply.Status);
        Assert.Contains("already been claimed", concurrentApply.Summary, StringComparison.Ordinal);
        Assert.Equal(1, runner.ApplyCalls);

        allowApply.TrySetResult(true);
        OperationResponse firstResponse = await firstApply;
        Assert.Equal("infrastructure-provisioned", firstResponse.Status);
        Assert.Equal(1, runner.ApplyCalls);
    }

    [Fact]
    public async Task Destroy_RequiresBreakGlassBeforeStartingTerraform()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "destroy", "{}", true, "destroy:aws-ecs:dev");

        Assert.Equal("break-glass-required", response.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Variables_RejectSecretsAndUnknownPathsBeforeProcessStart()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse secret = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"honua_admin_password\":\"do-not-log\"}", false, string.Empty);
        OperationResponse stack = await toolkit.ProvisionInfrastructureAsync(
            "../../other", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("variables-invalid", secret.Status);
        Assert.Equal("unsupported-stack", stack.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal("<redacted>", Redaction.ScrubValue("variablesJson", "{\"safe\":\"still-private\"}"));
    }

    [Fact]
    public async Task InstallHandoff_TakesEndpointAndSecretRefFromTheOperatorContract()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, runner, gateway);
        string outputDirectory = Path.Combine(root.Path, "handoff");

        // Neither the endpoint nor the secret reference is supplied by the caller.
        OperationResponse response = await toolkit.InstallHandoffAsync(
            "aws-ecs",
            baseUrl: string.Empty,
            adminKeySecretRef: string.Empty,
            outputDirectory,
            overwrite: false,
            provisioningOperationId: apply.ProvisioningLineage!.ProvisioningOperationId);

        Assert.Equal("install-handoff-written", response.Status);
        string proxyConfig = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "honua-mcp-proxy.handoff.json"));
        using JsonDocument handoff = JsonDocument.Parse(proxyConfig);
        JsonElement handoffRoot = handoff.RootElement;

        Assert.Equal("operator-contract", handoffRoot.GetProperty("endpointSource").GetString());
        Assert.Equal("operator-contract", handoffRoot.GetProperty("adminKeySecretRefSource").GetString());
        Assert.Equal(
            ProvisioningSubstrateFixtures.ContractEndpoint,
            handoffRoot.GetProperty("env").GetProperty("HONUA_BASE_URL").GetString());
        Assert.Equal(
            ProvisioningSubstrateFixtures.ContractAdminSecretRef,
            handoffRoot.GetProperty("secretRefs").GetProperty("HONUA_ADMIN_KEY").GetString());
        Assert.Equal("qualified", handoffRoot.GetProperty("operatorContract").GetProperty("status").GetString());

        // The old `honua_url` scrape is gone: the contract outputs are what is read.
        Assert.Contains(runner.Calls, call => call.Operation == "output");
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("taken from the stack's operator contract", StringComparison.Ordinal));

        // And the emitted document satisfies its own published contract.
        Assert.Empty(ProvisioningContracts.ValidateProxyHandoff(proxyConfig));
    }

    [Fact]
    public async Task InstallHandoff_FlagsACallerSuppliedEndpointAsAnOverride()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, runner, gateway);
        string outputDirectory = Path.Combine(root.Path, "override-handoff");

        OperationResponse response = await toolkit.InstallHandoffAsync(
            "aws-ecs",
            baseUrl: "https://tunnel.example.test",
            adminKeySecretRef: string.Empty,
            outputDirectory,
            overwrite: false,
            provisioningOperationId: apply.ProvisioningLineage!.ProvisioningOperationId);

        Assert.Equal("install-handoff-written", response.Status);
        string proxyConfig = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "honua-mcp-proxy.handoff.json"));
        using JsonDocument handoff = JsonDocument.Parse(proxyConfig);

        Assert.Equal("caller-override", handoff.RootElement.GetProperty("endpointSource").GetString());
        // The secret reference was NOT overridden, so it stays contract-sourced.
        Assert.Equal("operator-contract", handoff.RootElement.GetProperty("adminKeySecretRefSource").GetString());
        // What the stack reported is retained alongside the override, so the two can be compared.
        Assert.Equal(
            ProvisioningSubstrateFixtures.ContractEndpoint,
            handoff.RootElement.GetProperty("operatorContract").GetProperty("endpoint").GetString());
        Assert.Contains(
            response.Findings,
            finding => finding.Contains("WARNING: a caller argument overrode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallHandoff_RefusesWhenTheApplyEvidenceCarriesNoOperatorContract()
    {
        // Provisioning evidence produced before contract consumption cannot back a
        // handoff: there is nothing to prove the endpoint is the deployed one.
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.InstallHandoffAsync(
            "aws-ecs",
            "https://honua.example.test",
            "secret://HONUA_TEST_ADMIN_KEY",
            Path.Combine(root.Path, "no-evidence"),
            overwrite: false,
            provisioningOperationId: $"urn:honua:provisioning:{new string('0', 32)}");

        Assert.Equal("provisioning-evidence-missing", response.Status);
    }

    [Fact]
    public async Task VerifyHandoff_BindingNamesTheStateBackendAndIdentityThatProducedIt()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(succeed: true);
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, runner, gateway, verifier);

        string handoffDirectory = Path.Combine(root.Path, "verified-handoff");
        await toolkit.InstallHandoffAsync(
            "aws-ecs", string.Empty, string.Empty, handoffDirectory, false,
            apply.ProvisioningLineage!.ProvisioningOperationId);

        OperationResponse verified = await toolkit.VerifyInstallHandoffAsync(
            Path.Combine(handoffDirectory, "honua-mcp-proxy.handoff.json"), false);

        Assert.Equal("install-handoff-verified", verified.Status);
        string bindingJson = await File.ReadAllTextAsync(
            Path.Combine(handoffDirectory, "honua-devops-aws-ecs-provision-binding.json"));

        // The binding satisfies its published contract...
        Assert.Empty(ProvisioningContracts.ValidateProvisionBinding(bindingJson));

        using JsonDocument binding = JsonDocument.Parse(bindingJson);
        JsonElement execution = binding.RootElement.GetProperty("iacExecution");

        // ...and it names which state, under which identity, produced the claim.
        Assert.Equal(
            "0ea699e4b738a98ed5c9a7ce497ad94b7922fe0d1e86c88e38ba5be3902036a3",
            execution.GetProperty("backend").GetProperty("backendConfigDigest").GetString());
        Assert.Equal("s3", execution.GetProperty("backend").GetProperty("backendKind").GetString());
        Assert.Equal(
            "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
            execution.GetProperty("state").GetProperty("lineageAfter").GetString());
        Assert.Equal(13, execution.GetProperty("state").GetProperty("serialAfter").GetInt32());
        Assert.Equal(12, execution.GetProperty("state").GetProperty("serialBefore").GetInt32());
        Assert.Equal(
            "arn:aws:sts::123456789012:assumed-role/honua-deploy-dev/session",
            execution.GetProperty("executionIdentity").GetProperty("assumedRoleArn").GetString());
        Assert.Equal("sts-assumed-role", execution.GetProperty("executionIdentity").GetProperty("credentialKind").GetString());
        Assert.Equal(
            ProvisioningSubstrateFixtures.FixturePlanMetadataDigest,
            execution.GetProperty("planMetadataDigest").GetString());

        // The endpoint is the stack's, and the binding says so.
        Assert.Equal("operator-contract", binding.RootElement.GetProperty("endpointSource").GetString());
        Assert.Equal(
            ProvisioningSubstrateFixtures.ContractEndpoint,
            binding.RootElement.GetProperty("endpoint").GetString());

        // actuatorReceiptReference resolves: it content-addresses the carried receipt.
        string reference = binding.RootElement.GetProperty("lineage").GetProperty("actuatorReceiptReference").GetString()!;
        Assert.StartsWith("urn:sha256:", reference, StringComparison.Ordinal);
        Assert.Equal(reference[11..], binding.RootElement.GetProperty("execReceiptSha256").GetString());
        Assert.Equal(
            "honua.iac.exec-receipt",
            binding.RootElement.GetProperty("execReceipt").GetProperty("kind").GetString());

        // The teardown handle names a target a holder can actually act on.
        JsonElement teardown = binding.RootElement.GetProperty("teardownHandle");
        Assert.Equal("honua.iac.terraform-teardown/v1", teardown.GetProperty("kind").GetString());
        Assert.Equal("infrastructure/terraform/examples/aws", teardown.GetProperty("terraformRoot").GetString());
        Assert.Equal("destroy", teardown.GetProperty("action").GetString());
        Assert.Equal("honua/aws/dev/terraform.tfstate", teardown.GetProperty("objectKey").GetString());

        // And the verification receipt satisfies its own new contract.
        string receiptJson = await File.ReadAllTextAsync(
            Path.Combine(handoffDirectory, "honua-install-verification.receipt.json"));
        Assert.Empty(ProvisioningContracts.ValidateVerificationReceipt(receiptJson));
    }

    [Fact]
    public async Task VerifyHandoff_FailedProbeEmitsNoReadyBinding()
    {
        using TerraformTestRoot root = new();
        string config = Path.Combine(root.Path, "missing.json");
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway);

        OperationResponse response = await toolkit.VerifyInstallHandoffAsync(config, false);

        Assert.Equal("handoff-config-missing", response.Status);
        Assert.False(File.Exists(Path.Combine(root.Path, "aws-ecs-provision-binding.json")));
    }

    /// <summary>Plans and applies through the substrate, returning an execute-tier toolkit.</summary>
    private static async Task<(HonuaOperationsToolkit Toolkit, OperationResponse Apply)> ApplyAsync(
        TerraformTestRoot root,
        FakeSubstrateRunner runner,
        BackendGateway gateway,
        IInstallHandoffVerifier? verifier = null)
    {
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");

        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner,
            installHandoffVerifier: verifier);
        OperationResponse apply = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, approval);
        Assert.Equal("infrastructure-provisioned", apply.Status);
        return (toolkit, apply);
    }

    private static void DeleteSavedPlanFrom(ProcessCall planCall)
    {
        string? directory = Path.GetDirectoryName(planCall.Option("--plan-out")!);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
