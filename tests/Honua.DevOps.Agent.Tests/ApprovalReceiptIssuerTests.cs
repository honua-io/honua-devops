using System.Text.Json;
using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// The provision-approval receipt issuer (2026.1 rc.3 fix unit C1): a separate principal
/// turns a reviewed plan response into a receipt the applying agent accepts.
/// </summary>
/// <remarks>
/// Every round trip here crosses the real verifier in
/// <see cref="HonuaOperationsToolkit.ProvisionInfrastructureAsync"/>; the KMS boundary is
/// the stubbed <see cref="RecordedKmsMacClient"/>, so nothing here is evidence that the
/// live GenerateMac/VerifyMac split is configured.
/// </remarks>
public sealed class ApprovalReceiptIssuerTests
{
    private const string Issuer = ProvisioningSubstrateFixtures.ApprovalIssuer;
    private const string KeyArn = ProvisioningSubstrateFixtures.ApprovalKeyArn;

    private static Dictionary<string, string> IssuerKeyArns()
        => new(StringComparer.Ordinal) { [Issuer] = KeyArn };

    [Fact]
    public async Task KmsMac_IssuedReceiptIsAcceptedByTheVerifierAndIsEvidence()
    {
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            RecordedKmsMacClient signerPrincipal = RecordedKmsMacClient.Signer();
            IssuedApprovalReceipt issued = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.KmsMac),
                new KmsMacApprovalSignatureProvider(signerPrincipal, IssuerKeyArns()));

            Assert.True(issued.Evidentiary);
            Assert.Single(signerPrincipal.GenerateMacRequests);
            Assert.Empty(signerPrincipal.VerifyMacRequests);

            RecordedKmsMacClient verifierPrincipal = RecordedKmsMacClient.Verifier();
            OperationResponse response = await KmsExecutor(root, gateway, runner, verifierPrincipal)
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, issued.ReceiptJson);

            Assert.Equal("infrastructure-provisioned", response.Status);
            Assert.DoesNotContain(response.Findings, f => f.Contains("NON-EVIDENTIARY", StringComparison.Ordinal));
            Assert.Contains(response.Findings, f => f.Contains(issued.ApprovalReceiptId, StringComparison.Ordinal));
            Assert.Empty(verifierPrincipal.GenerateMacRequests);
            Assert.Single(verifierPrincipal.VerifyMacRequests);
        }
    }

    [Fact]
    public async Task IssuedReceiptIsSchemaValidBoundToThePlanAndDefaultsToFifteenMinutes()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            DateTimeOffset now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
            IssuedApprovalReceipt issued = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.KmsMac),
                new KmsMacApprovalSignatureProvider(RecordedKmsMacClient.Signer(), IssuerKeyArns()),
                now);

            Assert.Empty(ProvisioningContracts.ValidateProvisionApproval(issued.ReceiptJson));

            using JsonDocument receipt = JsonDocument.Parse(issued.ReceiptJson);
            JsonElement r = receipt.RootElement;
            ProvisioningLineage lineage = plan.ProvisioningLineage!;
            Assert.Equal(lineage.ProvisioningOperationId, r.GetProperty("provisioningOperationId").GetString());
            Assert.Equal(lineage.PlanSha256, r.GetProperty("planSha256").GetString(), ignoreCase: true);
            Assert.Equal(lineage.PlanMetadataDigest, r.GetProperty("planMetadataDigest").GetString(), ignoreCase: true);
            Assert.Equal("aws-ecs", r.GetProperty("stack").GetString());
            Assert.Equal("dev", r.GetProperty("environment").GetString());
            Assert.Equal("apply", r.GetProperty("action").GetString());
            Assert.Equal("approved", r.GetProperty("decision").GetString());
            Assert.Equal(KmsMacApprovalSignatureProvider.KeyIdForArn(KeyArn), r.GetProperty("keyId").GetString());
            Assert.Equal(
                TimeSpan.FromMinutes(ApprovalReceiptIssuer.DefaultTtlMinutes),
                r.GetProperty("expiresAtUtc").GetDateTimeOffset() - r.GetProperty("issuedAtUtc").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task PlanResponseCarriesTheStructuredApprovalRequest()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            ProvisionApprovalRequest request = Assert.IsType<ProvisionApprovalRequest>(plan.ApprovalRequest);
            Assert.Equal(plan.ProvisioningLineage!.ProvisioningOperationId, request.ProvisioningOperationId);
            Assert.Equal(plan.ProvisioningLineage.PlanMetadataDigest, request.PlanMetadataDigest);
            Assert.Equal("apply", request.Action);
            Assert.Equal("aws-ecs", request.Stack);
            Assert.Equal("dev", request.Environment);

            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(plan));
            Assert.True(document.RootElement.TryGetProperty("approvalRequest", out _));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(120)]
    public async Task TtlOutsideOneToSixtyMinutesIsRefused(int ttl)
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            ApprovalIssueRefusedException refused = await Assert.ThrowsAsync<ApprovalIssueRefusedException>(
                () => ApprovalReceiptIssuer.IssueAsync(
                    new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.KmsMac, TtlMinutes: ttl),
                    new KmsMacApprovalSignatureProvider(RecordedKmsMacClient.Signer(), IssuerKeyArns())));
            Assert.Equal("ttl-out-of-range", refused.Code);
        }
    }

    [Fact]
    public async Task SixtyMinuteTtlIsTheCeilingAndIsAccepted()
    {
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            IssuedApprovalReceipt issued = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.KmsMac, TtlMinutes: 60),
                new KmsMacApprovalSignatureProvider(RecordedKmsMacClient.Signer(), IssuerKeyArns()));

            OperationResponse response = await KmsExecutor(root, gateway, runner, RecordedKmsMacClient.Verifier())
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, issued.ReceiptJson);
            Assert.Equal("infrastructure-provisioned", response.Status);
        }
    }

    [Fact]
    public async Task Issuer_RefusesLocalHmacDevOutsideDev()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("staging");
        using (root)
        using (gateway)
        {
            ApprovalIssueRefusedException refused = await Assert.ThrowsAsync<ApprovalIssueRefusedException>(
                () => ApprovalReceiptIssuer.IssueAsync(
                    new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.LocalHmacDev),
                    ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider()));
            Assert.Equal("local-hmac-dev-environment-refused", refused.Code);
        }
    }

    [Fact]
    public async Task Verifier_RefusesLocalHmacDevReceiptOutsideDev()
    {
        // Built WITHOUT the issuer (which would refuse), to prove the verifier refuses
        // independently: a hand-made local receipt for staging authorizes nothing.
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync("staging");
        using (root)
        using (gateway)
        {
            string receipt = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply", environment: "staging");

            OperationResponse response = await new HonuaOperationsToolkit(
                    ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
                    gateway,
                    ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
                    provisioningProcessRunner: runner)
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"staging\"}", true, challenge, receipt);

            Assert.Equal("confirmation-required", response.Status);
            Assert.Contains("refused outside environment `dev`", response.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Any(a => a.EndsWith("terraform-exact-apply.sh", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task LocalHmacDev_RoundTripsInDevAndIsNonEvidentiary()
    {
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            IssuedApprovalReceipt issued = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.LocalHmacDev),
                ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider());
            Assert.False(issued.Evidentiary);

            OperationResponse response = await new HonuaOperationsToolkit(
                    ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
                    gateway,
                    ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
                    provisioningProcessRunner: runner)
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, issued.ReceiptJson);

            Assert.Equal("infrastructure-provisioned", response.Status);
            Assert.Contains(response.Findings, f => f.Contains("NON-EVIDENTIARY", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Runtime_RefusesLocalHmacDevWithIssuerKeysWhenAllowedEnvironmentsAreWiderThanDev()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_SIGNING_MODE", ApprovalSigningModes.LocalHmacDev);
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEYS", $"{Issuer}={Convert.ToBase64String(ProvisioningSubstrateFixtures.ApprovalKey)}");
        scope.Set("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", "dev,staging");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(OperationRuntime.Load);
        Assert.Contains("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", error.Message, StringComparison.Ordinal);

        scope.Set("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", "dev");
        OperationRuntime runtime = OperationRuntime.Load();
        Assert.Equal(ApprovalSigningModes.LocalHmacDev, runtime.ProvisionApprovalSigningMode);
    }

    [Fact]
    public void Runtime_LocalHmacDevWithoutIssuerKeysStaysInert()
    {
        // No keys = the local provider accepts nothing, so a wide allowlist is harmless
        // and existing non-provisioning operators keep starting.
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_SIGNING_MODE", null);
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEYS", null);
        scope.Set("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", "dev,staging,prod");

        OperationRuntime runtime = OperationRuntime.Load();
        Assert.Empty(runtime.ProvisionApprovalIssuerKeys!);
    }

    [Fact]
    public async Task ActionThatContradictsThePlanIsRefused()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            ApprovalIssueRefusedException refused = await Assert.ThrowsAsync<ApprovalIssueRefusedException>(
                () => ApprovalReceiptIssuer.IssueAsync(
                    new ApprovalIssueRequest(SerializeLikeMcp(plan), "destroy", Issuer, ApprovalSigningModes.KmsMac),
                    new KmsMacApprovalSignatureProvider(RecordedKmsMacClient.Signer(), IssuerKeyArns())));
            Assert.Equal("action-mismatch", refused.Code);
        }
    }

    [Fact]
    public async Task FlagOverridesMayFillButNeverContradictThePlan()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            KmsMacApprovalSignatureProvider provider = new(RecordedKmsMacClient.Signer(), IssuerKeyArns());

            ApprovalIssueRefusedException contradiction = await Assert.ThrowsAsync<ApprovalIssueRefusedException>(
                () => ApprovalReceiptIssuer.IssueAsync(
                    new ApprovalIssueRequest(SerializeLikeMcp(plan), "apply", Issuer, ApprovalSigningModes.KmsMac, Environment: "staging"),
                    provider));
            Assert.Equal("environment-mismatch", contradiction.Code);

            // A legacy response without approvalRequest: lineage only.
            string legacy = JsonSerializer.Serialize(new
            {
                status = "terraform-plan-ready",
                provisioningLineage = plan.ProvisioningLineage,
            });
            ApprovalIssueRefusedException missing = await Assert.ThrowsAsync<ApprovalIssueRefusedException>(
                () => ApprovalReceiptIssuer.IssueAsync(
                    new ApprovalIssueRequest(legacy, "apply", Issuer, ApprovalSigningModes.KmsMac),
                    provider));
            Assert.Equal("stack-missing", missing.Code);

            IssuedApprovalReceipt filled = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(legacy, "apply", Issuer, ApprovalSigningModes.KmsMac, Stack: "aws-ecs", Environment: "dev"),
                provider);
            Assert.Empty(ProvisioningContracts.ValidateProvisionApproval(filled.ReceiptJson));
        }
    }

    [Fact]
    public async Task AcceptsAnMcpToolsCallResultEnvelope()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            string envelope = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 7,
                result = new { content = new[] { new { type = "text", text = SerializeLikeMcp(plan) } }, isError = false },
            });

            IssuedApprovalReceipt issued = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(envelope, "apply", Issuer, ApprovalSigningModes.KmsMac),
                new KmsMacApprovalSignatureProvider(RecordedKmsMacClient.Signer(), IssuerKeyArns()));
            Assert.Empty(ProvisioningContracts.ValidateProvisionApproval(issued.ReceiptJson));
        }
    }

    [Fact]
    public async Task RefusesAResponseThatIsNotAReadyPlan()
    {
        string refusal = JsonSerializer.Serialize(new { status = "confirmation-required", summary = "no" });
        ApprovalIssueRefusedException refused = await Assert.ThrowsAsync<ApprovalIssueRefusedException>(
            () => ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(refusal, "apply", Issuer, ApprovalSigningModes.KmsMac),
                new KmsMacApprovalSignatureProvider(RecordedKmsMacClient.Signer(), IssuerKeyArns())));
        Assert.Equal("plan-response-not-ready", refused.Code);
    }

    [Fact]
    public async Task ItEnvironmentIsInTheSmallProvisioningLane()
    {
        // The release harness provisions its cells with environment=it.
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("it", ["it"]);
        using (root)
        using (gateway)
        {
            Assert.Equal("terraform-plan-ready", plan.Status);
            Assert.Equal("it", plan.ApprovalRequest!.Environment);
        }
    }

    // ----------------------------------------------------------------------
    // CLI
    // ----------------------------------------------------------------------

    [Fact]
    public void Cli_ParsesIssueProvisionApproval()
    {
        CliOptions options = CliOptions.Parse(
        [
            "--issue-provision-approval",
            "--from-plan-response", "plan.json",
            "--action", "APPLY",
            "--signing-mode", "kms-mac",
            "--issuer", "honua-release-approver",
            "--ttl-minutes", "30",
            "--decision", "rejected",
        ]);

        IssueProvisionApprovalCliOptions issue = Assert.IsType<IssueProvisionApprovalCliOptions>(options.IssueProvisionApproval);
        Assert.Equal("plan.json", issue.PlanResponsePath);
        Assert.Equal("apply", issue.Action);
        Assert.Equal(ApprovalSigningModes.KmsMac, issue.SigningMode);
        Assert.Equal("honua-release-approver", issue.Issuer);
        Assert.Equal(30, issue.TtlMinutes);
        Assert.Equal("rejected", issue.Decision);
        Assert.Null(issue.Stack);
    }

    [Fact]
    public void Cli_DefaultsTtlAndDecision()
    {
        CliOptions options = CliOptions.Parse(
        [
            "--issue-provision-approval", "--from-plan-response", "p.json", "--action", "destroy",
            "--signing-mode", "local-hmac-dev", "--issuer", "me",
        ]);

        Assert.Equal(ApprovalReceiptIssuer.DefaultTtlMinutes, options.IssueProvisionApproval!.TtlMinutes);
        Assert.Equal("approved", options.IssueProvisionApproval.Decision);
    }

    [Theory]
    [InlineData("--ttl-minutes", "61")]
    [InlineData("--ttl-minutes", "0")]
    [InlineData("--ttl-minutes", "abc")]
    [InlineData("--signing-mode", "plaintext")]
    [InlineData("--action", "plan")]
    [InlineData("--decision", "maybe")]
    public void Cli_RefusesInvalidValues(string flag, string value)
    {
        List<string> args =
        [
            "--issue-provision-approval", "--from-plan-response", "p.json", "--action", "apply",
            "--signing-mode", "kms-mac", "--issuer", "me",
        ];
        int existing = args.IndexOf(flag);
        if (existing >= 0)
        {
            args[existing + 1] = value;
        }
        else
        {
            args.AddRange([flag, value]);
        }

        Assert.Throws<InvalidOperationException>(() => CliOptions.Parse([.. args]));
    }

    [Fact]
    public void Cli_RequiresTheMandatoryFlags()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => CliOptions.Parse(["--issue-provision-approval", "--action", "apply"]));
        Assert.Contains("--from-plan-response", error.Message, StringComparison.Ordinal);
        Assert.Contains("--issuer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_ApprovalFlagsRequireTheModeFlag()
    {
        Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["--issuer", "me"]));
    }

    [Fact]
    public void Cli_HelpDocumentsTheIssuer()
    {
        Assert.Contains("--issue-provision-approval", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--from-plan-response", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--ttl-minutes", CliOptions.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_WritesOnlyTheReceiptToStdout()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("dev");
        using (root)
        using (gateway)
        {
            string planPath = Path.Combine(root.Path, "plan.json");
            await File.WriteAllTextAsync(planPath, SerializeLikeMcp(plan));
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exit = await IssueProvisionApprovalCommand.RunAsync(
                new IssueProvisionApprovalCliOptions(planPath, "apply", ApprovalSigningModes.KmsMac, Issuer, 15, "approved", null, null),
                stdout,
                stderr,
                readVariable: name => name == "HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEY_ARNS" ? $"{Issuer}={KeyArn}" : null,
                kmsMacClient: RecordedKmsMacClient.Signer());

            Assert.Equal(0, exit);
            Assert.Empty(ProvisioningContracts.ValidateProvisionApproval(stdout.ToString()));
            Assert.Contains("Issued approval-", stderr.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Command_RefusalWritesNothingToStdout()
    {
        (OperationResponse plan, _, TerraformTestRoot root, _, BackendGateway gateway) = await PlanAsync("staging");
        using (root)
        using (gateway)
        {
            string planPath = Path.Combine(root.Path, "plan.json");
            await File.WriteAllTextAsync(planPath, SerializeLikeMcp(plan));
            StringWriter stdout = new();
            StringWriter stderr = new();

            int exit = await IssueProvisionApprovalCommand.RunAsync(
                new IssueProvisionApprovalCliOptions(planPath, "apply", ApprovalSigningModes.LocalHmacDev, Issuer, 15, "approved", null, null),
                stdout,
                stderr,
                readVariable: name => name == "HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEYS"
                    ? $"{Issuer}={Convert.ToBase64String(ProvisioningSubstrateFixtures.ApprovalKey)}"
                    : null);

            Assert.Equal(1, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.StartsWith("local-hmac-dev-environment-refused", stderr.ToString(), StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------------------------

    /// <summary>
    /// The MCP host serializes tool results with camelCase web defaults; the plan file an
    /// approver receives is that document.
    /// </summary>
    private static string SerializeLikeMcp(OperationResponse plan)
        => JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<(OperationResponse Plan, string Challenge, TerraformTestRoot Root, FakeSubstrateRunner Runner, BackendGateway Gateway)> PlanAsync(
        string environment,
        string[]? allowedEnvironments = null)
    {
        TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan);
        if (allowedEnvironments is not null)
        {
            runtime = runtime with { AllowedEnvironments = allowedEnvironments };
        }

        HonuaOperationsToolkit planner = new(runtime, gateway, provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", $"{{\"environment\":\"{environment}\"}}", false, string.Empty);
        Assert.Equal("terraform-plan-ready", plan.Status);
        return (plan, ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), root, runner, gateway);
    }

    private static HonuaOperationsToolkit KmsExecutor(
        TerraformTestRoot root,
        BackendGateway gateway,
        FakeSubstrateRunner runner,
        RecordedKmsMacClient verifierPrincipal)
        => new(
            ProvisioningSubstrateFixtures.CreateRuntime(
                root.Path,
                ExecutionMode.Execute,
                ExecutionTier.ExecuteLowerEnv,
                ApprovalSigningModes.KmsMac),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner,
            approvalSignatureProvider: new KmsMacApprovalSignatureProvider(verifierPrincipal, IssuerKeyArns()));
}
