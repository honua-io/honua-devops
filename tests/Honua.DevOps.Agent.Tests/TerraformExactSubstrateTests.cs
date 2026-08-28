using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Eval;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// Consumption of the honua-iac governed execution substrate (honua-iac#149/#158):
/// locating it, refusing without it, surfacing its typed refusals, and reading the
/// documents it emits.
/// </summary>
public sealed class TerraformExactSubstrateTests
{
    [Fact]
    public async Task Plan_RefusesWhenTheCheckoutPredatesTheExactPlanSubstrate()
    {
        // A honua-iac checkout without the wrappers must fail closed. Falling back to
        // hand-rolled `terraform init/plan` would silently drop the backend identity,
        // the short-lived-identity check and the whole refusal matrix.
        using TerraformTestRoot root = new(withSubstrateScripts: false);
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("iac-substrate-unavailable", response.Status);
        Assert.Contains("terraform-exact-plan.sh", response.Summary, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Plan_RefusesARootThatCarriesNoCommittedProviderLock()
    {
        // The substrate refuses an unpinnable root with `provider-lock-missing`;
        // naming one is a configuration error worth reporting before a process starts.
        using TerraformTestRoot root = new(withProviderLock: false);
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("terraform-root-invalid", response.Status);
        Assert.Contains(".terraform.lock.hcl", response.Summary, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void QualifiedRoot_RefusesTraversalOutOfTheExamplesDirectory()
    {
        using TerraformTestRoot root = new();
        Assert.True(TerraformExactSubstrate.TryResolve(root.Path, out TerraformExactSubstrate? substrate, out _));

        // A bare directory name is the only accepted form, so no caller-controlled
        // string can walk out of the examples tree.
        foreach (string candidate in new[] { "../../..", "aws/../../modules", "/etc", "aws root", string.Empty })
        {
            Assert.Throws<InvalidOperationException>(() => substrate!.ResolveQualifiedRoot(candidate));
        }

        Assert.Equal(root.AwsRoot, substrate!.ResolveQualifiedRoot("aws"));
    }

    [Theory]
    [InlineData("local-state-refused")]
    [InlineData("state-serial-drift")]
    [InlineData("account-mismatch")]
    [InlineData("long-lived-credential-refused")]
    public async Task Plan_SurfacesAWrapperRefusalAsItsOwnTypedStatus(string reason)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new() { PlanRefusalReason = reason };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);

        // The specific cause survives to the caller instead of collapsing into a
        // generic "terraform failed": these reasons have different fixes.
        Assert.Equal($"iac-refused-{reason}", response.Status);
        Assert.NotEqual("terraform-plan-failed", response.Status);
        Assert.Contains($"REFUSED[{reason}]", response.Summary, StringComparison.Ordinal);
        Assert.Contains(response.Findings, finding => finding.Contains($"Refusal reason: {reason}", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("documented row of the fail-closed matrix", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_SurfacesARefusalAsAPreMutationDecisionNotAFailedApply()
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
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        runner.ApplyRefusalReason = "state-serial-drift";

        HonuaOperationsToolkit executor = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, approval);

        Assert.Equal("iac-refused-state-serial-drift", response.Status);

        // A refusal happens before any mutation, so the response must not send the
        // operator hunting for partially-created resources that cannot exist.
        Assert.Contains("Nothing was mutated by this call.", response.Risks);
        Assert.DoesNotContain(
            response.Risks,
            risk => risk.Contains("partially-created", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Refusal_ParsesTheWrapperContractAndIgnoresOrdinaryFailures()
    {
        Assert.True(TerraformExactRefusal.TryParse(
            new ProvisioningProcessResult(3, string.Empty, "[ERROR] REFUSED[backend-substituted]: digest moved", false),
            out TerraformExactRefusal? refusal));
        Assert.Equal("backend-substituted", refusal!.Reason);
        Assert.Equal("iac-refused-backend-substituted", refusal.Status);
        Assert.True(refusal.IsKnown);

        // An unknown reason is still surfaced — the substrate may be newer than this
        // build — but is reported as unrecognized rather than silently trusted.
        Assert.True(TerraformExactRefusal.TryParse(
            new ProvisioningProcessResult(3, string.Empty, "REFUSED[a-brand-new-reason]: something", false),
            out TerraformExactRefusal? unknown));
        Assert.False(unknown!.IsKnown);

        // An ordinary Terraform error is NOT a governed refusal.
        Assert.False(TerraformExactRefusal.TryParse(
            new ProvisioningProcessResult(1, string.Empty, "Error: invalid provider configuration", false),
            out _));

        // Neither is a timeout, even if output happens to contain the token.
        Assert.False(TerraformExactRefusal.TryParse(
            new ProvisioningProcessResult(-1, "REFUSED[plan-expired]", string.Empty, true),
            out _));
    }

    [Fact]
    public void OperatorContract_ReadsTheThreeStructuredOutputsAndValidatesThem()
    {
        Assert.True(OperatorContract.TryRead(
            ProvisioningSubstrateFixtures.TerraformOutputJson,
            ProvisioningSubstrateFixtures.OperatorContractSchemaJson,
            out OperatorContract? contract,
            out string error));
        Assert.Equal(string.Empty, error);

        Assert.Equal(ProvisioningSubstrateFixtures.ContractEndpoint, contract!.Endpoint);
        Assert.Equal(ProvisioningSubstrateFixtures.ContractAdminSecretRef, contract.AdminKeySecretRef);
        Assert.True(contract.IsQualified);
        Assert.Equal("c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4", contract.BackendConfigDigest);
        Assert.Equal("8f3b1c2d-4e5a-4b6c-9d7e-0a1b2c3d4e5f", contract.StateLineage);
        Assert.Equal(42, contract.StateSerial);
        Assert.Equal("123456789012", contract.AccountId);
        Assert.Equal("us-east-1", contract.Region);
    }

    [Fact]
    public void OperatorContract_RefusesWhenTheStackProjectsNoContract()
    {
        // The pre-#147 behaviour was to scrape `honua_url`. A stack that emits only
        // that must now be refused, not quietly accepted.
        const string scalarOnly = """
        {"honua_url":{"sensitive":false,"type":"string","value":"https://honua.example.test"}}
        """;

        Assert.False(OperatorContract.TryRead(
            scalarOnly,
            ProvisioningSubstrateFixtures.OperatorContractSchemaJson,
            out _,
            out string error));
        Assert.Contains("deployment_contract", error, StringComparison.Ordinal);
        Assert.Contains("scraping a scalar URL output", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorContract_RefusesAnIdentityThatDisagreesAcrossTheThreeOutputs()
    {
        // Three outputs assembled from different applies describe no single stack.
        JsonNode outputs = JsonNode.Parse(ProvisioningSubstrateFixtures.TerraformOutputJson)!;
        outputs["validation_contract"]!["value"]!["identity"]!["state_lineage"] = "ffffffff-4e5a-4b6c-9d7e-0a1b2c3d4e5f";

        Assert.False(OperatorContract.TryRead(
            outputs.ToJsonString(),
            ProvisioningSubstrateFixtures.OperatorContractSchemaJson,
            out _,
            out string error));
        Assert.Contains("do not describe one apply", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorContract_RefusesADigestThatDisagreesWithTheStacksOwnOutput()
    {
        JsonNode outputs = JsonNode.Parse(ProvisioningSubstrateFixtures.TerraformOutputJson)!;
        outputs["operator_contract_digest"]!["value"] = new string('b', 64);

        Assert.False(OperatorContract.TryRead(
            outputs.ToJsonString(),
            ProvisioningSubstrateFixtures.OperatorContractSchemaJson,
            out _,
            out string error));
        Assert.Contains("does not match `identity.contract_digest`", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactPlanMetadata_ReadsTheSubstrateDocumentAgainstItsPublishedSchema()
    {
        Assert.True(ExactPlanMetadata.TryRead(
            ProvisioningSubstrateFixtures.ExactPlanMetadataJson,
            ProvisioningSubstrateFixtures.ExactPlanSchemaJson,
            out ExactPlanMetadata? metadata,
            out string error));
        Assert.Equal(string.Empty, error);

        Assert.Equal(ProvisioningSubstrateFixtures.FixturePlanMetadataDigest, metadata!.PlanMetadataDigest);
        Assert.Equal("0ea699e4b738a98ed5c9a7ce497ad94b7922fe0d1e86c88e38ba5be3902036a3", metadata.BackendConfigDigest);
        Assert.Equal("s3", metadata.BackendKind);
        Assert.True(metadata.BackendIsRemote);
        Assert.Equal("arn:aws:sts::123456789012:assumed-role/honua-deploy-dev/session", metadata.AssumedRoleArn);
        Assert.Equal("sts-assumed-role", metadata.CredentialKind);
        Assert.Equal(12, metadata.StateSerialBefore);

        // The capture ran offline, so it is stamped as such and is not release evidence.
        Assert.True(metadata.IsOfflineEvidence);
        Assert.False(metadata.ReleaseQualified);
    }

    [Fact]
    public void ExecReceipt_ReadsTheSubstrateDocumentAndJoinsToItsPlan()
    {
        Assert.True(TerraformExecReceipt.TryRead(
            ProvisioningSubstrateFixtures.ExecReceiptJson,
            ProvisioningSubstrateFixtures.ExecReceiptSchemaJson,
            out TerraformExecReceipt? receipt,
            out string error));
        Assert.Equal(string.Empty, error);

        Assert.True(receipt!.Succeeded);
        Assert.Equal(ProvisioningSubstrateFixtures.FixturePlanMetadataDigest, receipt.PlanMetadataDigest);
        Assert.Equal(ProvisioningSubstrateFixtures.FixturePlanMetadataDigest, receipt.ApprovedDigest);
        Assert.Equal(13, receipt.StateSerialAfter);
        Assert.Equal("infrastructure/terraform/examples/aws", receipt.TeardownRoot);
        Assert.Equal("destroy", receipt.TeardownAction);
    }

    [Fact]
    public void ExactPlanMetadata_RejectsADocumentThatViolatesTheSubstrateSchema()
    {
        JsonNode metadata = JsonNode.Parse(ProvisioningSubstrateFixtures.ExactPlanMetadataJson)!;
        metadata.AsObject().Remove("state_before");

        Assert.False(ExactPlanMetadata.TryRead(
            metadata.ToJsonString(),
            ProvisioningSubstrateFixtures.ExactPlanSchemaJson,
            out _,
            out string error));
        Assert.Contains("state_before", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HonuaIacSchemas_AreFullyEnforceableByTheValidator()
    {
        // The validator is fail-closed on keywords it cannot enforce, so this asserts
        // honua-devops genuinely validates these contracts rather than partially
        // understanding them.
        foreach (string schema in new[]
        {
            ProvisioningSubstrateFixtures.OperatorContractSchemaJson,
            ProvisioningSubstrateFixtures.ExactPlanSchemaJson,
            ProvisioningSubstrateFixtures.ExecReceiptSchemaJson
        })
        {
            Assert.Empty(JsonSchemaValidator.CheckSchemaSupport(schema));
        }
    }
}
