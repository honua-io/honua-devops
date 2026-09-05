using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// The approval-receipt signing split (honua-devops#175).
/// </summary>
/// <remarks>
/// What these tests are for: a receipt is only evidence if the party that accepts it
/// could not have produced it. Under <c>local-hmac-dev</c> it demonstrably could, so those
/// receipts are marked non-evidentiary; under <c>kms-mac</c> the capability is split across
/// two IAM principals and neither can do the other's half. Everything here runs against a
/// stubbed KMS client — no live call, and no claim that the live split is configured.
/// </remarks>
public sealed class ApprovalReceiptSigningTests
{
    private const string Issuer = ProvisioningSubstrateFixtures.ApprovalIssuer;
    private const string KeyArn = ProvisioningSubstrateFixtures.ApprovalKeyArn;
    private const string OtherKeyArn = ProvisioningSubstrateFixtures.OtherApprovalKeyArn;

    private static Dictionary<string, string> IssuerKeyArns(string arn = KeyArn)
        => new(StringComparer.Ordinal) { [Issuer] = arn };

    [Fact]
    public async Task KmsMac_SignerHoldingGenerateMacCannotVerify()
    {
        // The whole point of the split: a principal that can issue receipts must not be
        // able to accept them, or it can approve its own mutations.
        RecordedKmsMacClient signerPrincipal = RecordedKmsMacClient.Signer();
        KmsMacApprovalSignatureProvider signer = new(signerPrincipal, IssuerKeyArns());

        ApprovalSignature signature = await signer.SignAsync(Issuer, "payload");

        ApprovalVerificationResult verification =
            await signer.VerifyAsync(Issuer, "payload", signature.Signature);

        Assert.False(verification.Verified);
        Assert.False(verification.Evidentiary);
        Assert.Contains("kms:VerifyMac was denied", verification.Detail, StringComparison.Ordinal);

        // It really did attempt the call and really was refused by the stubbed IAM
        // boundary, rather than being short-circuited somewhere earlier.
        Assert.Single(signerPrincipal.GenerateMacRequests);
        Assert.Single(signerPrincipal.VerifyMacRequests);
    }

    [Fact]
    public async Task KmsMac_VerifierHoldingVerifyMacCannotSign()
    {
        RecordedKmsMacClient verifierPrincipal = RecordedKmsMacClient.Verifier();
        KmsMacApprovalSignatureProvider verifier = new(verifierPrincipal, IssuerKeyArns());

        await Assert.ThrowsAsync<KmsMacAccessDeniedException>(
            () => verifier.SignAsync(Issuer, "payload"));

        Assert.Single(verifierPrincipal.GenerateMacRequests);
    }

    [Fact]
    public async Task KmsMac_RoundTripsAcrossTwoSeparatelyPermissionedPrincipals()
    {
        // Neither side holds both capabilities, and the receipt still verifies. Without
        // this, "split the permissions" could be satisfied by a scheme that simply
        // never works.
        RecordedKmsMacClient signerPrincipal = RecordedKmsMacClient.Signer();
        RecordedKmsMacClient verifierPrincipal = RecordedKmsMacClient.Verifier();
        KmsMacApprovalSignatureProvider signer = new(signerPrincipal, IssuerKeyArns());
        KmsMacApprovalSignatureProvider verifier = new(verifierPrincipal, IssuerKeyArns());

        ApprovalSignature signature = await signer.SignAsync(Issuer, "canonical-payload");
        ApprovalVerificationResult verification =
            await verifier.VerifyAsync(Issuer, "canonical-payload", signature.Signature);

        Assert.True(verification.Verified);
        Assert.True(verification.Evidentiary);
        Assert.Equal(ApprovalSigningModes.KmsMac, verification.SigningMode);

        // The key id names the key without disclosing it, and is derivable from the ARN
        // by both principals — neither can hash key material it is never given.
        Assert.Equal(KmsMacApprovalSignatureProvider.KeyIdForArn(KeyArn), signature.KeyId);
        Assert.Equal(KmsMacAlgorithms.HmacSha256, Assert.Single(signerPrincipal.GenerateMacRequests).MacAlgorithm);
        Assert.Equal(KeyArn, Assert.Single(verifierPrincipal.VerifyMacRequests).KeyId);
    }

    [Fact]
    public async Task KmsMac_RejectsAReceiptSignedWithADifferentKey()
    {
        // A real second key, not a corrupted MAC: the failure has to come from the key
        // identity, which is what an attacker with their own KMS key would present.
        KmsMacApprovalSignatureProvider signerOnOtherKey =
            new(RecordedKmsMacClient.Signer(), IssuerKeyArns(OtherKeyArn));
        KmsMacApprovalSignatureProvider verifier =
            new(RecordedKmsMacClient.Verifier(), IssuerKeyArns(KeyArn));

        ApprovalSignature foreign = await signerOnOtherKey.SignAsync(Issuer, "canonical-payload");
        ApprovalVerificationResult verification =
            await verifier.VerifyAsync(Issuer, "canonical-payload", foreign.Signature);

        Assert.False(verification.Verified);
        Assert.False(verification.Evidentiary);
        Assert.Contains("could not be verified", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KmsMac_RejectsAnIssuerOutsideTheAllowlist()
    {
        KmsMacApprovalSignatureProvider verifier =
            new(RecordedKmsMacClient.Verifier(), IssuerKeyArns());

        ApprovalVerificationResult verification =
            await verifier.VerifyAsync("test://not-configured", "payload", "AAAA");

        Assert.False(verification.Verified);
        Assert.Contains("trusted-issuer allowlist", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalHmac_VerifiesButIsNeverEvidentiary()
    {
        // The forgeability is not hypothetical: the same provider that accepts the
        // receipt just produced it. That is why the mode is marked non-evidentiary
        // rather than merely discouraged.
        IApprovalSignatureProvider provider = ProvisioningSubstrateFixtures.LocalApprovalSignatureProvider();

        ApprovalSignature signature = await provider.SignAsync(Issuer, "canonical-payload");
        ApprovalVerificationResult verification =
            await provider.VerifyAsync(Issuer, "canonical-payload", signature.Signature);

        Assert.True(verification.Verified);
        Assert.False(verification.Evidentiary);
        Assert.False(ApprovalSigningModes.IsEvidentiary(ApprovalSigningModes.LocalHmacDev));
        Assert.True(ApprovalSigningModes.IsEvidentiary(ApprovalSigningModes.KmsMac));
        Assert.Contains("NOT evidence", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSigningModeIsNeitherKnownNorEvidentiary()
    {
        // Fail closed: a mode nobody has implemented must not inherit evidentiary weight
        // by default.
        Assert.False(ApprovalSigningModes.IsKnown("kms-sign"));
        Assert.False(ApprovalSigningModes.IsKnown(null));
        Assert.False(ApprovalSigningModes.IsEvidentiary("kms-sign"));
    }

    [Fact]
    public void RuntimeRefusesKmsMacWithoutKeyArns()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_SIGNING_MODE", ApprovalSigningModes.KmsMac);
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEY_ARNS", null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(OperationRuntime.Load);
        Assert.Contains("ISSUER_KEY_ARNS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRefusesAnIssuerKeyEntryThatIsNotAKmsArn()
    {
        // This variable must never be able to carry key material, and an alias or bare
        // key id would let a caller retarget verification to a key the operator did not
        // name.
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_SIGNING_MODE", ApprovalSigningModes.KmsMac);
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEY_ARNS", $"{Issuer}=alias/honua-approval-mac");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(OperationRuntime.Load);
        Assert.Contains("full KMS key ARN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRefusesAnUnknownSigningMode()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_PROVISION_APPROVAL_SIGNING_MODE", "plaintext");

        Assert.Throws<InvalidOperationException>(OperationRuntime.Load);
    }

    // ----------------------------------------------------------------------
    // End to end through the provisioning tool.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Apply_RefusesAnApprovalReceiptThatDeclaresNoSigningMode()
    {
        // The verifier will not guess how a signature was made. A receipt without the
        // field fails the published schema, which the verifier now checks first.
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync();
        using (root)
        using (gateway)
        {
            string receipt = ProvisioningSubstrateFixtures.CreateApprovalReceipt(
                plan, "apply", omitSigningMode: true);

            OperationResponse response = await ExecutorFor(root, gateway, runner)
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, receipt);

            Assert.Equal("confirmation-required", response.Status);
            Assert.Contains("provision-approval/v1", response.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
        }
    }

    [Fact]
    public async Task Apply_RefusesAReceiptWhoseSigningModeIsNotTheOneThisVerifierRuns()
    {
        // An issuer must not get to choose the weaker primitive on the verifier's behalf.
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync();
        using (root)
        using (gateway)
        {
            string receipt = ProvisioningSubstrateFixtures.CreateApprovalReceipt(
                plan, "apply", declaredSigningMode: ApprovalSigningModes.KmsMac);

            OperationResponse response = await ExecutorFor(root, gateway, runner)
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, receipt);

            Assert.Equal("confirmation-required", response.Status);
            Assert.Contains("this verifier is configured for", response.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
        }
    }

    [Fact]
    public async Task Apply_MarksALocalHmacApprovalNonEvidentiary()
    {
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync();
        using (root)
        using (gateway)
        {
            string receipt = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");

            OperationResponse response = await ExecutorFor(root, gateway, runner)
                .ProvisionInfrastructureAsync(
                    "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, receipt);

            Assert.Equal("infrastructure-provisioned", response.Status);
            string finding = Assert.Single(
                response.Findings!,
                f => f.Contains("NON-EVIDENTIARY", StringComparison.Ordinal));
            Assert.Contains(ApprovalSigningModes.LocalHmacDev, finding, StringComparison.Ordinal);
            Assert.Contains(ApprovalSigningModes.KmsMac, finding, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Apply_AcceptsAKmsMacApprovalAndReportsItAsEvidence()
    {
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync();
        using (root)
        using (gateway)
        {
            // The issuer signs with GenerateMac only; the agent verifies with VerifyMac
            // only. Two principals, two clients, neither able to do the other's half.
            KmsMacApprovalSignatureProvider issuer =
                new(RecordedKmsMacClient.Signer(), IssuerKeyArns());
            RecordedKmsMacClient verifierPrincipal = RecordedKmsMacClient.Verifier();

            string receipt = ProvisioningSubstrateFixtures.CreateApprovalReceipt(
                plan, "apply", signatureProvider: issuer);

            HonuaOperationsToolkit executor = new(
                ProvisioningSubstrateFixtures.CreateRuntime(
                    root.Path,
                    ExecutionMode.Execute,
                    ExecutionTier.ExecuteLowerEnv,
                    ApprovalSigningModes.KmsMac),
                gateway,
                ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
                provisioningProcessRunner: runner,
                approvalSignatureProvider: new KmsMacApprovalSignatureProvider(
                    verifierPrincipal, IssuerKeyArns()));

            OperationResponse response = await executor.ProvisionInfrastructureAsync(
                "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, receipt);

            Assert.Equal("infrastructure-provisioned", response.Status);
            Assert.DoesNotContain(response.Findings!, f => f.Contains("NON-EVIDENTIARY", StringComparison.Ordinal));
            Assert.Single(response.Findings!, f => f.Contains("kms:VerifyMac only", StringComparison.Ordinal));

            // The verifier never attempted to sign anything.
            Assert.Empty(verifierPrincipal.GenerateMacRequests);
            Assert.Single(verifierPrincipal.VerifyMacRequests);
        }
    }

    [Fact]
    public async Task Apply_RefusesAKmsMacReceiptSignedUnderADifferentKey()
    {
        (OperationResponse plan, string challenge, TerraformTestRoot root, FakeSubstrateRunner runner, BackendGateway gateway) =
            await PlanAsync();
        using (root)
        using (gateway)
        {
            KmsMacApprovalSignatureProvider foreignIssuer =
                new(RecordedKmsMacClient.Signer(), IssuerKeyArns(OtherKeyArn));
            string receipt = ProvisioningSubstrateFixtures.CreateApprovalReceipt(
                plan, "apply", signatureProvider: foreignIssuer);

            HonuaOperationsToolkit executor = new(
                ProvisioningSubstrateFixtures.CreateRuntime(
                    root.Path,
                    ExecutionMode.Execute,
                    ExecutionTier.ExecuteLowerEnv,
                    ApprovalSigningModes.KmsMac),
                gateway,
                ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
                provisioningProcessRunner: runner,
                approvalSignatureProvider: new KmsMacApprovalSignatureProvider(
                    RecordedKmsMacClient.Verifier(), IssuerKeyArns(KeyArn)));

            OperationResponse response = await executor.ProvisionInfrastructureAsync(
                "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, receipt);

            // The key id names the other key, so this is refused before any MAC is
            // computed — the receipt says outright which key must verify it.
            Assert.Equal("confirmation-required", response.Status);
            Assert.Contains("key id", response.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(runner.Calls, call => call.Operation == "terraform-exact-apply.sh");
        }
    }

    [Fact]
    public void TheReceiptFixtureSatisfiesThePublishedSchema()
    {
        // The canonical payload and the document have to agree about the new field, and
        // the schema is what honua-release#129 will issue against.
        string receipt = JsonSerializer.Serialize(new
        {
            schemaVersion = "honua.devops.provision-approval/v1",
            approvalReceiptId = "approval-1",
            issuer = Issuer,
            keyId = KmsMacApprovalSignatureProvider.KeyIdForArn(KeyArn),
            provisioningOperationId = $"urn:honua:provisioning:{new string('a', 32)}",
            planSha256 = new string('b', 64),
            planMetadataDigest = new string('c', 64),
            action = "apply",
            stack = "aws-ecs",
            environment = "dev",
            decision = "approved",
            signingMode = ApprovalSigningModes.KmsMac,
            issuedAtUtc = "2026-08-29T00:00:00.0000000+00:00",
            expiresAtUtc = "2026-08-29T00:15:00.0000000+00:00",
            signature = Convert.ToBase64String(Encoding.UTF8.GetBytes("mac")),
        });

        Assert.Empty(ProvisioningContracts.ValidateProvisionApproval(receipt));

        string withoutMode = receipt.Replace(
            $",\"signingMode\":\"{ApprovalSigningModes.KmsMac}\"",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEmpty(ProvisioningContracts.ValidateProvisionApproval(withoutMode));
    }

    // ----------------------------------------------------------------------

    private static async Task<(OperationResponse Plan, string Challenge, TerraformTestRoot Root, FakeSubstrateRunner Runner, BackendGateway Gateway)> PlanAsync()
    {
        TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        return (plan, ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="), root, runner, gateway);
    }

    private static HonuaOperationsToolkit ExecutorFor(
        TerraformTestRoot root,
        BackendGateway gateway,
        FakeSubstrateRunner runner)
        => new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);
}
