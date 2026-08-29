using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Eval;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// The published honua-devops provisioning contracts under <c>contracts/</c>.
/// </summary>
/// <remarks>
/// These schemas are enforced on write, so a schema that drifts out of the
/// validator's enforceable subset would silently downgrade "validated" to
/// "partially validated" in production. That is asserted here rather than
/// discovered when an artifact is emitted.
/// </remarks>
public sealed class ProvisioningContractSchemaTests
{
    [Fact]
    public void EveryProvisioningContractIsEmbeddedParsableAndFullyEnforceable()
    {
        foreach (string resource in ProvisioningContracts.AllResources)
        {
            string schema = ProvisioningContracts.Read(resource);
            using JsonDocument document = JsonDocument.Parse(schema);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(document.RootElement.TryGetProperty("$id", out _), $"{resource} has no $id.");

            IReadOnlyList<string> unsupported = JsonSchemaValidator.CheckSchemaSupport(schema);
            Assert.True(
                unsupported.Count == 0,
                $"{resource} uses constructs the validator cannot enforce: {string.Join("; ", unsupported)}");
        }
    }

    [Fact]
    public void ProvisionBinding_RejectsABindingMissingTheSubstrateEvidence()
    {
        JsonNode binding = JsonNode.Parse(ValidBindingJson)!;
        Assert.Empty(ProvisioningContracts.ValidateProvisionBinding(binding.ToJsonString()));

        // The whole point of this slice: a binding that cannot name which state,
        // backend and identity produced it is not a valid binding.
        JsonNode withoutExecution = JsonNode.Parse(ValidBindingJson)!;
        withoutExecution.AsObject().Remove("iacExecution");
        Assert.Contains(
            ProvisioningContracts.ValidateProvisionBinding(withoutExecution.ToJsonString()),
            error => error.Contains("iacExecution", StringComparison.Ordinal));

        // ...nor one that omits the backend identity inside it.
        JsonNode withoutBackend = JsonNode.Parse(ValidBindingJson)!;
        withoutBackend["iacExecution"]!.AsObject().Remove("backend");
        Assert.Contains(
            ProvisioningContracts.ValidateProvisionBinding(withoutBackend.ToJsonString()),
            error => error.Contains("backend", StringComparison.Ordinal));

        // ...nor one that omits the state lineage.
        JsonNode withoutState = JsonNode.Parse(ValidBindingJson)!;
        withoutState["iacExecution"]!.AsObject().Remove("state");
        Assert.NotEmpty(ProvisioningContracts.ValidateProvisionBinding(withoutState.ToJsonString()));

        // ...nor one that omits the execution role identity.
        JsonNode withoutIdentity = JsonNode.Parse(ValidBindingJson)!;
        withoutIdentity["iacExecution"]!.AsObject().Remove("executionIdentity");
        Assert.NotEmpty(ProvisioningContracts.ValidateProvisionBinding(withoutIdentity.ToJsonString()));

        // ...nor one that omits the plan-metadata digest the approval bound.
        JsonNode withoutDigest = JsonNode.Parse(ValidBindingJson)!;
        withoutDigest["lineage"]!.AsObject().Remove("planMetadataDigest");
        Assert.NotEmpty(ProvisioningContracts.ValidateProvisionBinding(withoutDigest.ToJsonString()));
    }

    [Fact]
    public void ProvisionBinding_RejectsAnUnresolvableActuatorReceiptReference()
    {
        // A reference the holder cannot dereference is worse than none, so the shape
        // that used to be emitted — `terraform://stack/action/sha` — is now invalid.
        JsonNode binding = JsonNode.Parse(ValidBindingJson)!;
        binding["lineage"]!["actuatorReceiptReference"] = "terraform://aws-ecs/apply/deadbeef";

        Assert.Contains(
            ProvisioningContracts.ValidateProvisionBinding(binding.ToJsonString()),
            error => error.Contains("actuatorReceiptReference", StringComparison.Ordinal));
    }

    [Fact]
    public void ProvisionBinding_RejectsAnUnqualifiedOperatorContract()
    {
        JsonNode binding = JsonNode.Parse(ValidBindingJson)!;
        binding["operatorContract"]!["status"] = "unqualified";

        Assert.NotEmpty(ProvisioningContracts.ValidateProvisionBinding(binding.ToJsonString()));
    }

    [Fact]
    public void ProvisionApproval_RequiresThePlanMetadataDigestTheSubstrateEnforces()
    {
        JsonNode approval = JsonNode.Parse(ValidApprovalJson)!;
        Assert.Empty(ProvisioningContracts.ValidateProvisionApproval(approval.ToJsonString()));

        approval.AsObject().Remove("planMetadataDigest");
        Assert.Contains(
            ProvisioningContracts.ValidateProvisionApproval(approval.ToJsonString()),
            error => error.Contains("planMetadataDigest", StringComparison.Ordinal));
    }

    [Fact]
    public void ProxyHandoff_RequiresAnExplicitEndpointProvenance()
    {
        JsonNode handoff = JsonNode.Parse(ValidHandoffJson)!;
        Assert.Empty(ProvisioningContracts.ValidateProxyHandoff(handoff.ToJsonString()));

        handoff.AsObject().Remove("endpointSource");
        Assert.Contains(
            ProvisioningContracts.ValidateProxyHandoff(handoff.ToJsonString()),
            error => error.Contains("endpointSource", StringComparison.Ordinal));

        JsonNode invalidSource = JsonNode.Parse(ValidHandoffJson)!;
        invalidSource["endpointSource"] = "trust-me";
        Assert.NotEmpty(ProvisioningContracts.ValidateProxyHandoff(invalidSource.ToJsonString()));
    }

    [Fact]
    public void ProxyHandoff_RejectsSecretMaterialInPlaceOfALocator()
    {
        JsonNode handoff = JsonNode.Parse(ValidHandoffJson)!;
        handoff["secretRefs"]!.AsObject().Remove("HONUA_ADMIN_KEY");
        Assert.NotEmpty(ProvisioningContracts.ValidateProxyHandoff(handoff.ToJsonString()));

        // An unversioned proxy cannot produce release-grade evidence.
        JsonNode unpinned = JsonNode.Parse(ValidHandoffJson)!;
        unpinned["proxyArtifact"]!["package"] = "@honua/mcp-server";
        Assert.NotEmpty(ProvisioningContracts.ValidateProxyHandoff(unpinned.ToJsonString()));
    }

    private const string Sha = "0ea699e4b738a98ed5c9a7ce497ad94b7922fe0d1e86c88e38ba5be3902036a3";
    private const string OperationId = "urn:honua:provisioning:0123456789abcdef0123456789abcdef";

    private static readonly string ValidBindingJson = $$"""
    {
      "schemaVersion": "honua.devops.aws-ecs-provision-binding/v1",
      "lineage": {
        "provisioningOperationId": "{{OperationId}}",
        "planSha256": "{{Sha}}",
        "planMetadataDigest": "{{Sha}}",
        "approvalReceiptId": "approval-1",
        "approvalReceiptSha256": "{{Sha}}",
        "applyAuditEventId": "0123456789abcdef0123456789abcdef",
        "actuatorReceiptReference": "urn:sha256:{{Sha}}",
        "handoffReceiptSha256": "{{Sha}}",
        "handoffVerificationReceiptId": "urn:sha256:{{Sha}}",
        "handoffVerificationReceiptSha256": "{{Sha}}",
        "rootProvisioningOperationId": "{{OperationId}}"
      },
      "endpoint": "https://honua.example.com",
      "endpointSource": "operator-contract",
      "adminKeySecretRefSource": "operator-contract",
      "candidateReference": "honua-2026.1.1",
      "proxyArtifact": { "package": "@honua/mcp-server@2026.1.1", "integrity": "sha512-dGVzdA==" },
      "secretReferenceSha256": "{{Sha}}",
      "handoffSha256": "{{Sha}}",
      "verificationReceiptId": "urn:sha256:{{Sha}}",
      "verificationReceiptSha256": "{{Sha}}",
      "operatorContract": {
        "digest": "{{Sha}}",
        "status": "qualified",
        "endpoint": "https://honua.example.com"
      },
      "iacExecution": {
        "planMetadataDigest": "{{Sha}}",
        "savedPlanSha256": "{{Sha}}",
        "terraformRoot": "aws",
        "iacRevision": "3c39cc9c54ba0000000000000000000000000000",
        "iacTreeDigest": "{{Sha}}",
        "terraformVersion": "1.10.5",
        "providerLockDigest": "{{Sha}}",
        "inputDigest": "{{Sha}}",
        "backend": {
          "backendConfigDigest": "{{Sha}}",
          "backendKind": "s3",
          "isRemote": true,
          "workspace": "default",
          "objectKey": "honua/aws/dev/terraform.tfstate",
          "region": "us-east-1"
        },
        "executionIdentity": {
          "assumedRoleArn": "arn:aws:sts::123456789012:assumed-role/honua-deploy-dev/session",
          "roleId": "AROAEXAMPLEID",
          "accountId": "123456789012",
          "partition": "aws",
          "issuer": "https://token.actions.githubusercontent.com",
          "credentialKind": "sts-assumed-role"
        },
        "state": {
          "lineageBefore": "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
          "serialBefore": 12,
          "lineageAfter": "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
          "serialAfter": 13
        },
        "operatorContractDigest": "{{Sha}}",
        "evidenceMode": "offline-test",
        "releaseQualified": false
      },
      "execReceiptSha256": "{{Sha}}",
      "execReceipt": {
        "schema_version": "v1",
        "kind": "honua.iac.exec-receipt",
        "plan_metadata_digest": "{{Sha}}",
        "state_after": { "lineage": "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", "serial": 13 }
      },
      "teardownHandle": {
        "kind": "honua.iac.terraform-teardown/v1",
        "terraformRoot": "infrastructure/terraform/examples/aws",
        "action": "destroy",
        "workspace": "default",
        "backendConfigDigest": "{{Sha}}",
        "objectKey": "honua/aws/dev/terraform.tfstate",
        "stateLineage": "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
        "stateSerial": 13,
        "accountId": "123456789012",
        "region": "us-east-1"
      }
    }
    """;

    private static readonly string ValidApprovalJson = $$"""
    {
      "schemaVersion": "honua.devops.provision-approval/v1",
      "approvalReceiptId": "approval-1",
      "issuer": "test://release-approver",
      "keyId": "0123456789abcdef",
      "provisioningOperationId": "{{OperationId}}",
      "planSha256": "{{Sha}}",
      "planMetadataDigest": "{{Sha}}",
      "action": "apply",
      "stack": "aws-ecs",
      "environment": "dev",
      "decision": "approved",
      "signingMode": "kms-mac",
      "issuedAtUtc": "2026-08-28T21:00:00Z",
      "expiresAtUtc": "2026-08-28T21:30:00Z",
      "signature": "ZmFrZS1zaWduYXR1cmU="
    }
    """;

    private static readonly string ValidHandoffJson = $$"""
    {
      "schemaVersion": "honua.mcp-proxy.handoff/v1",
      "rootProvisioningOperationId": "{{OperationId}}",
      "provisioningLineage": { "provisioningOperationId": "{{OperationId}}" },
      "candidateReference": "honua-2026.1.1",
      "endpointSource": "operator-contract",
      "adminKeySecretRefSource": "operator-contract",
      "operatorContract": {
        "digest": "{{Sha}}",
        "status": "qualified",
        "endpoint": "https://honua.example.com",
        "adminKeySecretRef": "arn:aws:secretsmanager:us-east-1:123456789012:secret:honua-admin"
      },
      "proxyArtifact": { "package": "@honua/mcp-server@2026.1.1", "integrity": "sha512-dGVzdA==" },
      "command": "npx",
      "args": ["-y", "--package", "@honua/mcp-server@2026.1.1", "honua-mcp-proxy"],
      "env": {
        "HONUA_BASE_URL": "https://honua.example.com",
        "HONUA_MCP_REMOTE_URL": "https://honua.example.com/mcp"
      },
      "secretRefs": { "HONUA_ADMIN_KEY": "arn:aws:secretsmanager:us-east-1:123456789012:secret:honua-admin" },
      "capabilityContract": {
        "verification": { "method": "MCP tools/list", "failClosed": true },
        "required": [
          {
            "name": "admin",
            "activation": "default operation family",
            "serverConfiguration": [],
            "requiredToolPrefixes": ["honua_admin_"],
            "requiredTools": ["honua_admin_server_status"]
          }
        ]
      }
    }
    """;
}
