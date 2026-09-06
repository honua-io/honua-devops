using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("plan")]
    [InlineData("approval")]
    [InlineData("plan-metadata")]
    [InlineData("apply")]
    [InlineData("approvalReceiptId")]
    [InlineData("planMetadataDigest")]
    [InlineData("rootProvisioningOperationId")]
    public async Task Review_ApplyReplayRejectsIncompleteOrSubstitutedLineage(string change)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, runner, gateway);
        ProvisioningLineage lineage = apply.ProvisioningLineage!;
        string approval = Encoding.UTF8.GetString(RetainedEvidence().Read(lineage.EvidenceRefs!.Single(r => r.Kind == "approval")));
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "honua-devops", "provisioning", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lineage.ProvisioningOperationId))) + ".json");
        JsonNode state = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonNode recorded = state["Lineage"]!;
        if (change == "absent") recorded.AsObject().Remove("evidenceRefs");
        else if (change == "empty") recorded["evidenceRefs"] = new JsonArray();
        else if (new[] { "plan", "approval", "plan-metadata", "apply" }.Contains(change))
        {
            JsonArray references = recorded["evidenceRefs"]!.AsArray();
            references.Remove(references.Single(r => r!["kind"]!.GetValue<string>() == change));
        }
        else recorded[change] = "substituted";
        await File.WriteAllTextAsync(path, state.ToJsonString());
        int calls = runner.Calls.Count;
        string token = lineage.ProvisioningOperationId.Split(':')[^1];
        OperationResponse replay = await toolkit.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true,
            $"apply:aws-ecs:dev:{token}", approval);
        Assert.Equal("lineage-evidence-invalid", replay.Status);
        Assert.Equal(calls, runner.Calls.Count);
        Assert.Equal(1, runner.ApplyCalls);
    }

    [Fact]
    public async Task Review_ProvisionToolAcceptsLegacyArgumentsWithoutIdempotencyKey()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        AIFunction tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(CapabilityToolset.Create(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway),
            t => t.Name == "provision_infrastructure"));
        Assert.DoesNotContain(tool.JsonSchema.GetProperty("required").EnumerateArray(), p => p.GetString() == "idempotencyKey");
        Assert.Equal("", tool.JsonSchema.GetProperty("properties").GetProperty("idempotencyKey").GetProperty("default").GetString());
        object? result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["stack"] = "unsupported", ["size"] = "small", ["action"] = "plan", ["variablesJson"] = "{}",
            ["confirmed"] = false, ["confirmation"] = "", ["approvalReceiptJson"] = ""
        });
        Assert.Equal("unsupported-stack", JsonSerializer.SerializeToElement(result).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("honua-install-verification.receipt.json", false)]
    [InlineData("honua-devops-aws-ecs-provision-binding.json", false)]
    [InlineData("honua-install-verification.receipt.json", true)]
    [InlineData("honua-devops-aws-ecs-provision-binding.json", true)]
    public async Task Review_VerificationRecoveryPreservesConflictingExportsUnlessOverwrite(string modifiedFile, bool copied)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeInstallHandoffVerifier verifier = new(true);
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway, verifier);
        string directory = Path.Combine(root.Path, "original");
        await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, apply.ProvisioningLineage!.ProvisioningOperationId);
        string config = Path.Combine(directory, "honua-mcp-proxy.handoff.json");
        Assert.Equal("install-handoff-verified", (await toolkit.VerifyInstallHandoffAsync(config, false)).Status);
        byte[] expected = await File.ReadAllBytesAsync(Path.Combine(directory, modifiedFile));
        if (copied)
        {
            directory = Path.Combine(root.Path, "copied");
            Directory.CreateDirectory(directory);
            File.Copy(config, Path.Combine(directory, Path.GetFileName(config)));
            config = Path.Combine(directory, Path.GetFileName(config));
        }
        string otherFile = modifiedFile == "honua-install-verification.receipt.json"
            ? "honua-devops-aws-ecs-provision-binding.json" : "honua-install-verification.receipt.json";
        File.Delete(Path.Combine(directory, otherFile));
        await File.WriteAllTextAsync(Path.Combine(directory, modifiedFile), "operator-modified");
        Assert.Equal("verification-evidence-exists", (await toolkit.VerifyInstallHandoffAsync(config, false)).Status);
        Assert.Equal("operator-modified", await File.ReadAllTextAsync(Path.Combine(directory, modifiedFile)));
        Assert.False(File.Exists(Path.Combine(directory, otherFile)));
        Assert.Equal("install-handoff-verified", (await toolkit.VerifyInstallHandoffAsync(config, true)).Status);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(directory, modifiedFile)));
        Assert.True(File.Exists(Path.Combine(directory, otherFile)));
        Assert.Equal(1, verifier.Calls);
    }
}
