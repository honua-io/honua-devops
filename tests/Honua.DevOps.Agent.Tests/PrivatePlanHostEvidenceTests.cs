using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class PrivatePlanHostEvidenceTests
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    [Fact]
    public async Task UnsupportedHostKeepsLegacyPlanButRefusesProtectedEvidenceExport()
    {
        if (PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("terraform-plan-ready", plan.Status);
        string path = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "private-host-evidence.json")));
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, await PrivatePlanHostEvidence.ExportAsync(
            plan.ProvisioningLineage!.ProvisioningOperationId, output, error));
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("unsupported-platform", error.ToString(), StringComparison.Ordinal);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void CanonicalDigestMatchesIacPythonUnicodeAndSortedKeyGolden()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"plan_metadata_digest":"0000000000000000000000000000000000000000000000000000000000000000","source":"Café 😀","a":{"z":1,"a":"a\nb"}}
            """);
        Assert.Equal("80b65963e788247c779c01f4bd593c1a1f07c08d2487557c79a0d2aadaf83069",
            ExactPlanCanonicalDigest.Compute(document.RootElement));
    }

    [Fact]
    public async Task IacSourceHelperAndExecutionReceiptUseTheRelativeLogicalRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        string helper = Path.Combine(root.Path, "tf-exec-contract.sh");
        await File.WriteAllTextAsync(helper,
            ProvisioningSubstrateFixtures.ReadFixture("cleanup/tf-exec-contract.sh"));
        ProvisioningProcessResult actual = await SystemProvisioningProcessRunner.Instance.RunAsync(
            "bash",
            ["-c", "source \"$1\"; iac_source_doc \"$2\" \"$3\" true", "source-root-contract",
                helper, root.Path, root.AwsRoot],
            root.Path, TimeSpan.FromSeconds(5));
        Assert.Equal(0, actual.ExitCode);
        using JsonDocument source = JsonDocument.Parse(actual.StandardOutput);
        string expected = Path.GetRelativePath(root.Path, root.AwsRoot);
        Assert.Equal(expected, source.RootElement.GetProperty("terraform_root").GetString());
        using JsonDocument retainedPlan = JsonDocument.Parse(ProvisioningSubstrateFixtures.ExactPlanMetadataJson);
        using JsonDocument retainedReceipt = JsonDocument.Parse(ProvisioningSubstrateFixtures.ExecReceiptJson);
        Assert.Equal(expected, retainedPlan.RootElement.GetProperty("source").GetProperty("terraform_root").GetString());
        Assert.Equal(expected,
            retainedReceipt.RootElement.GetProperty("cleanup").GetProperty("teardown_root").GetString());
    }

    [Fact]
    public async Task FifoAutoFileIsRejectedWithoutOpeningABlockingReader()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TerraformTestRoot root = new();
        string variableFile = Path.Combine(root.Path, "small.auto.tfvars.json");
        await File.WriteAllTextAsync(variableFile, "{}");
        string fifo = Path.Combine(root.AwsRoot, "blocking.auto.tfvars");
        Assert.Equal(0, MakeFifo(fifo, 0x180));

        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            PrivatePlanHostEvidence.Capture(root.AwsRoot, variableFile, null)).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EmptyRegularAutoFileRemainsPartOfTheInputClosure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TerraformTestRoot root = new();
        string variableFile = Path.Combine(root.Path, "small.auto.tfvars.json");
        await File.WriteAllTextAsync(variableFile, "{}");
        string empty = Path.Combine(root.AwsRoot, "empty.auto.tfvars");
        await File.WriteAllBytesAsync(empty, []);

        PrivatePlanHostEvidence.Snapshot witness = PrivatePlanHostEvidence.Capture(
            root.AwsRoot, variableFile, null);

        Assert.Contains($"autofile:empty.auto.tfvars={Hash([])}", witness.Entries);
    }

    [Fact]
    public async Task SnapshotHashesExactGeneratedVarfileAutoFilesAndTerraformVariableValues()
    {
        using TerraformTestRoot root = new();
        string variableFile = Path.Combine(root.Path, "small.auto.tfvars.json");
        await File.WriteAllTextAsync(variableFile, "{\"environment\":\"dev\"}");
        string autoFile = Path.Combine(root.AwsRoot, "extra.auto.tfvars.json");
        await File.WriteAllTextAsync(autoFile, "{\"region\":\"us-west-2\"}");
        var overlay = new Dictionary<string, string> { ["TF_VAR_fixture_name"] = "two words" };

        PrivatePlanHostEvidence.Snapshot witness = PrivatePlanHostEvidence.Capture(
            root.AwsRoot, variableFile, overlay);

        Assert.Contains($"varfile:{variableFile}={Hash(await File.ReadAllBytesAsync(variableFile))}", witness.Entries);
        Assert.Contains($"autofile:extra.auto.tfvars.json={Hash(await File.ReadAllBytesAsync(autoFile))}", witness.Entries);
        Assert.Contains($"env:fixture_name={Hash(Encoding.UTF8.GetBytes("two words"))}", witness.Entries);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(string.Join('\n', witness.Entries))), witness.InputDigest);
        Assert.Equal(witness.Entries.Select(entry => entry[..entry.IndexOf('=')]), witness.InputRefs);
    }

    [Fact]
    public async Task SnapshotRefusesCliArgumentsThatCouldAddUnobservedTerraformInputs()
    {
        using TerraformTestRoot root = new();
        string variableFile = Path.Combine(root.Path, "small.auto.tfvars.json");
        await File.WriteAllTextAsync(variableFile, "{}");

        Assert.Throws<InvalidDataException>(() => PrivatePlanHostEvidence.Capture(
            root.AwsRoot, variableFile,
            new Dictionary<string, string> { ["TF_CLI_ARGS_plan"] = "-var-file=another.tfvars" }));
    }

    [Fact]
    public void HostExportCliRequiresAnObservedOperationAndCannotBeCombinedWithMcp()
    {
        CliOptions options = CliOptions.Parse([
            "--export-provision-plan-host-evidence",
            "urn:honua:provisioning:00000000000000000000000000000000"
        ]);
        Assert.NotNull(options.ExportProvisionPlanHostEvidence);
        Assert.Throws<InvalidOperationException>(() => CliOptions.Parse([
            "--mcp", "--export-provision-plan-host-evidence",
            "urn:honua:provisioning:00000000000000000000000000000000"
        ]));
    }

    [Fact]
    public async Task PlanWithUnsupportedAutoInputsRefusesBeforeSubstrateExecution()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        await File.WriteAllTextAsync(Path.Combine(root.AwsRoot, "terraform.tfvars.json"), "{}");
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse result = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("exact-plan-input-unsupported", result.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MetadataInputMismatchRefusesAndDeletesUnapprovablePlan()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new() { PreservePlanInputMismatch = true };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse result = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("exact-plan-input-mismatch", result.Status);
        string planPath = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        Assert.False(Directory.Exists(Path.GetDirectoryName(planPath)));
        Assert.Null(result.ApprovalRequest);
    }

    [Fact]
    public async Task CanonicalMetadataDigestMismatchCannotProduceAnApprovablePlan()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new() { PreservePlanCanonicalMismatch = true };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse result = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("exact-plan-metadata-invalid", result.Status);
        string path = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Null(result.ApprovalRequest);
    }

    [Fact]
    public async Task HostExportBindsObservedOperationToSavedInputMetadataAndPlan()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        JsonNode fixture = JsonNode.Parse(ProvisioningSubstrateFixtures.ExactPlanMetadataJson)!;
        fixture["expires_at_utc"] = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O");
        runner.PlanDocumentJson = fixture.ToJsonString();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);
        Assert.Equal("terraform-plan-ready", plan.Status);
        string operationId = plan.ProvisioningLineage!.ProvisioningOperationId;
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            int result = await PrivatePlanHostEvidence.ExportAsync(operationId, output, error);
            Assert.Equal(0, result);
            Assert.Equal(string.Empty, error.ToString());
            using JsonDocument evidence = JsonDocument.Parse(output.ToString());
            JsonElement document = evidence.RootElement;
            Assert.Equal(operationId, document.GetProperty("provisioningOperationId").GetString());
            Assert.Equal(plan.ProvisioningLineage.PlanSha256, document.GetProperty("planSha256").GetString());
            Assert.Equal(root.AwsRoot, document.GetProperty("hostTerraformRoot").GetString());
            Assert.Equal(Path.GetRelativePath(root.Path, root.AwsRoot),
                document.GetProperty("sourceTerraformRoot").GetString());
            string[] entries = document.GetProperty("executedInputEntries").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            Assert.Contains(entries, entry => entry.StartsWith("autofile:terraform.tfvars=", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.StartsWith("varfile:", StringComparison.Ordinal));
            using JsonDocument variables = JsonDocument.Parse(document.GetProperty("executedVariablesJson").GetString()!);
            Assert.Equal("dev", variables.RootElement.GetProperty("environment").GetString());
            using JsonDocument metadata = JsonDocument.Parse(document.GetProperty("planMetadataJson").GetString()!);
            string digest = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', entries))));
            Assert.Equal(digest, metadata.RootElement.GetProperty("inputs").GetProperty("input_digest").GetString());

            string planPath = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
                .Option("--plan-out")!;
            await File.AppendAllTextAsync(planPath, "tampered");
            using StringWriter denied = new();
            Assert.Equal(1, await PrivatePlanHostEvidence.ExportAsync(operationId, denied, error));
            Assert.Equal(string.Empty, denied.ToString());
        }
        finally
        {
            string path = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
                .Option("--plan-out")!;
            if (Directory.Exists(Path.GetDirectoryName(path)))
                Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
