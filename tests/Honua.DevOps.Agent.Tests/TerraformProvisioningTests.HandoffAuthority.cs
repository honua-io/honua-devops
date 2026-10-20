using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Fact]
    public async Task Handoff_RefusesAnEnvironmentRemovedFromTheCurrentSession()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway);
        string directory = Path.Combine(root.Path, "allowed-original");
        Assert.Equal("install-handoff-written", (await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false,
            apply.ProvisioningLineage!.ProvisioningOperationId)).Status);
        FakeInstallHandoffVerifier verifier = new(true);
        HonuaOperationsToolkit restricted = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv)
                with { AllowedEnvironments = ["staging"] },
            gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), installHandoffVerifier: verifier);
        string output = Path.Combine(root.Path, "not-created");
        Assert.Equal("environment-not-allowed", (await restricted.InstallHandoffAsync("aws-ecs", "", "", output, false,
            apply.ProvisioningLineage.ProvisioningOperationId)).Status);
        Assert.False(Directory.Exists(output));
        Assert.Equal("environment-not-allowed", (await restricted.VerifyInstallHandoffAsync(
            Path.Combine(directory, "honua-mcp-proxy.handoff.json"), false)).Status);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData((int)ExecutionMode.Plan, (int)ExecutionTier.Plan, true, "execution-mode-gated")]
    [InlineData((int)ExecutionMode.Execute, (int)ExecutionTier.Observe, true, "execution-tier-gated")]
    [InlineData((int)ExecutionMode.Execute, (int)ExecutionTier.ExecuteLowerEnv, false, "approval-mode-gated")]
    public async Task Handoff_AuthorityPolicyRefusesBeforeFilesOrVerifier(
        int mode, int tier, bool direct, string expected)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(true);
        HonuaOperationsToolkit toolkit = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, (ExecutionMode)mode, (ExecutionTier)tier),
            gateway, direct ? ProvisioningSubstrateFixtures.DirectAllowedPolicy() : null, installHandoffVerifier: verifier);
        string output = Path.Combine(root.Path, "refused-handoff");
        OperationResponse result = await toolkit.InstallHandoffAsync("aws-ecs", "", "", output, false,
            "urn:honua:provisioning:" + new string('a', 32));
        Assert.Equal(expected, result.Status);
        Assert.False(Directory.Exists(output));
        string config = Path.Combine(root.Path, "unread-config.json");
        await File.WriteAllTextAsync(config, "not even JSON");
        Assert.Equal(expected, (await toolkit.VerifyInstallHandoffAsync(config, false)).Status);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("secret")]
    [InlineData("remote")]
    public async Task LegacyHandoff_WithValidRecordedHashesStillCannotOverrideAppliedAuthority(string mutation)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(true);
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway, verifier);
        string directory = Path.Combine(root.Path, "legacy-handoff");
        string config = Path.Combine(directory, "honua-mcp-proxy.handoff.json");
        OperationResponse handoff = await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false,
            apply.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal("install-handoff-written", handoff.Status);
        JsonObject document = JsonNode.Parse(await File.ReadAllTextAsync(config))!.AsObject();
        if (mutation == "endpoint") document["env"]!["HONUA_BASE_URL"] = "https://foreign.invalid/";
        else if (mutation == "remote") document["env"]!["HONUA_MCP_REMOTE_URL"] = "https://foreign.invalid/mcp";
        else document["secretRefs"]!["HONUA_ADMIN_KEY"] = "arn:aws:secretsmanager:us-east-1:123456789012:secret:foreign";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        ProvisioningEvidenceStore store = RetainedEvidence();
        ProvisioningEvidenceReference reference = store.Put("handoff", bytes);
        ProvisioningLineage lineage = handoff.ProvisioningLineage! with
        {
            HandoffReceiptSha256 = reference.Sha256,
            EvidenceRefs = [.. handoff.ProvisioningLineage!.EvidenceRefs!.Where(value => value.Kind != "handoff"), reference]
        };
        // Model a historically accepted override: all retained byte hashes and lineage joins are valid.
        store.ValidateAppliedLineage(lineage);
        Assert.Equal(reference.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        string statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "honua-devops", "provisioning",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lineage.ProvisioningOperationId))) + ".json");
        JsonObject state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
        state["Lineage"] = JsonSerializer.SerializeToNode(lineage);
        await File.WriteAllTextAsync(statePath, state.ToJsonString());
        await File.WriteAllBytesAsync(config, bytes);

        OperationResponse result = await toolkit.VerifyInstallHandoffAsync(config, false);

        Assert.Equal("runtime-handoff-evidence-mismatch", result.Status);
        Assert.Equal(0, verifier.Calls);
        Assert.False(File.Exists(Path.Combine(directory, "honua-install-verification.receipt.json")));
        Assert.False(File.Exists(Path.Combine(directory, "honua-devops-aws-ecs-provision-binding.json")));
    }
}
