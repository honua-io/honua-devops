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
    private const string NativeEksVariables = """
        {
          "region":"us-west-2","environment":"dev","cluster_name":"honua-dev","target_id":"eks-dev",
          "tenant_id":"tenant-dev","image_reference":"ghcr.io/honua-io/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "gp_job_image_reference":"ghcr.io/honua-io/gp@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "server_role_arn":"arn:aws:iam::123456789012:role/honua-server",
          "gp_job_role_arn":"arn:aws:iam::123456789012:role/honua-gp",
          "server_pod_identity_association_id":"a-server","gp_job_pod_identity_association_id":"a-gp",
          "public_hostname":"dev.example.com",
          "certificate_arn":"arn:aws:acm:us-west-2:123456789012:certificate/11111111-2222-3333-4444-555555555555",
          "load_balancer_controller":"aws-load-balancer-controller",
          "db_connection_secret_arn":"arn:aws:secretsmanager:us-west-2:123456789012:secret:db-AbCdEf",
          "admin_password_secret_arn":"arn:aws:secretsmanager:us-west-2:123456789012:secret:admin-AbCdEf",
          "master_key_secret_arn":"arn:aws:secretsmanager:us-west-2:123456789012:secret:master-AbCdEf",
          "artifact_capabilities":{
            "server_image_digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "gp_job_image_digest":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "supported_workload_identities":["pod_identity"]
          }
        }
        """;

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
    public async Task LegacySnapshotCanMatchCanonicalInputsButCannotQualifySecretBearingHostExport()
    {
        using TerraformTestRoot root = new();
        string variableFile = Path.Combine(root.Path, "small.auto.tfvars.json");
        await File.WriteAllTextAsync(variableFile, "{\"environment\":\"dev\"}");
        string autoFile = Path.Combine(root.AwsRoot, "extra.auto.tfvars.json");
        await File.WriteAllTextAsync(autoFile, "{\"region\":\"us-west-2\"}");
        var overlay = new Dictionary<string, string> { ["TF_VAR_fixture_name"] = "two words" };

        PrivatePlanHostEvidence.Snapshot witness = PrivatePlanHostEvidence.Capture(
            root.AwsRoot, variableFile, overlay);

        Assert.False(PrivatePlanHostEvidence.IsExportEligibleInputClosure(root.AwsRoot, overlay));

        Assert.Contains($"varfile:{variableFile}={Hash(await File.ReadAllBytesAsync(variableFile))}", witness.Entries);
        Assert.Contains($"autofile:extra.auto.tfvars.json={Hash(await File.ReadAllBytesAsync(autoFile))}", witness.Entries);
        Assert.Contains($"env:fixture_name={Hash(Encoding.UTF8.GetBytes("two words"))}", witness.Entries);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(string.Join('\n', witness.Entries))), witness.InputDigest);
        Assert.Equal(witness.Entries.Select(entry => entry[..entry.IndexOf('=')]), witness.InputRefs);
    }

    [Fact]
    public async Task OnlyGeneratedNonsecretVarfileQualifiesForProtectedHostWitness()
    {
        using TerraformTestRoot root = new();
        File.Delete(Path.Combine(root.AwsRoot, "terraform.tfvars"));
        string variableFile = Path.Combine(root.Path, "small.auto.tfvars.json");
        await File.WriteAllTextAsync(variableFile, "{}");

        Assert.True(PrivatePlanHostEvidence.IsExportEligibleInputClosure(root.AwsRoot, null));
        PrivatePlanHostEvidence.Snapshot witness = PrivatePlanHostEvidence.Capture(root.AwsRoot, variableFile, null);
        Assert.Single(witness.Entries);
        Assert.True(PrivatePlanHostEvidence.IsSafeGeneratedOnlySnapshot(witness, variableFile));
        Assert.StartsWith($"varfile:{variableFile}=", witness.Entries[0], StringComparison.Ordinal);
        Assert.False(PrivatePlanHostEvidence.MatchesMetadata(witness,
            ProvisioningSubstrateFixtures.ExactPlanMetadataJson));
        Assert.False(PrivatePlanHostEvidence.IsExportEligibleInputClosure(root.AwsRoot,
            new Dictionary<string, string> { ["TF_VAR_password"] = "guessable" }));
    }

    [Fact]
    public async Task NativeEksGeneratedSecretReferencesPermitExactProtectedHostExport()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new(rootName: "aws-eks-native");
        File.Delete(Path.Combine(root.AwsRoot, "terraform.tfvars"));
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(
            root.Path, ExecutionMode.Plan, ExecutionTier.Plan) with
        {
            RuntimeKind = "aws-eks", RuntimeBackend = "honua-kubernetes-deployment",
            DeployTargetId = "eks-dev", ServerTargetId = "server-eks-dev",
            TerraformDeploymentTargets = ["eks"]
        };
        HonuaOperationsToolkit toolkit = new(runtime, gateway, provisioningProcessRunner: runner);

        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync(
            "aws-eks", "small", "plan", NativeEksVariables, false, string.Empty);

        Assert.Equal("terraform-plan-ready", plan.Status);
        Assert.Contains(plan.Findings, finding => finding.Contains("Protected host evidence: eligible", StringComparison.Ordinal));
        string planPath = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        string directory = Path.GetDirectoryName(planPath)!;
        try
        {
            Assert.True(File.Exists(Path.Combine(directory, "private-host-evidence.json")));
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(0, await PrivatePlanHostEvidence.ExportAsync(
                plan.ProvisioningLineage!.ProvisioningOperationId, output, error));
            Assert.Equal(string.Empty, error.ToString());
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            string entry = Assert.Single(document.RootElement.GetProperty("executedInputEntries")
                .EnumerateArray().Select(item => item.GetString()!));
            Assert.StartsWith("varfile:", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("env:", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("autofile:", output.ToString(), StringComparison.Ordinal);
            byte[] originalPlan = await File.ReadAllBytesAsync(planPath);
            await File.AppendAllTextAsync(planPath, "tampered");
            using StringWriter deniedPlan = new();
            Assert.Equal(1, await PrivatePlanHostEvidence.ExportAsync(
                plan.ProvisioningLineage.ProvisioningOperationId, deniedPlan, error));
            Assert.Equal(string.Empty, deniedPlan.ToString());
            await File.WriteAllBytesAsync(planPath, originalPlan);
            string sidecarPath = Path.Combine(directory, "private-host-evidence.json");
            JsonNode sidecar = JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath))!;
            sidecar["ExecutedInputEntries"]!.AsArray()[0] = entry[..(entry.IndexOf('=') + 1)] + new string('0', 64);
            await File.WriteAllTextAsync(sidecarPath, sidecar.ToJsonString());
            using StringWriter deniedEntry = new();
            Assert.Equal(1, await PrivatePlanHostEvidence.ExportAsync(
                plan.ProvisioningLineage.ProvisioningOperationId, deniedEntry, error));
            Assert.Equal(string.Empty, deniedEntry.ToString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NativeEksEligibleInputMismatchRefusesPlanAndDeletesUnapprovableDirectory()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new(rootName: "aws-eks-native");
        File.Delete(Path.Combine(root.AwsRoot, "terraform.tfvars"));
        FakeSubstrateRunner runner = new() { PreservePlanInputMismatch = true };
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(
            root.Path, ExecutionMode.Plan, ExecutionTier.Plan) with
        {
            RuntimeKind = "aws-eks", RuntimeBackend = "honua-kubernetes-deployment",
            DeployTargetId = "eks-dev", ServerTargetId = "server-eks-dev",
            TerraformDeploymentTargets = ["eks"]
        };
        HonuaOperationsToolkit toolkit = new(runtime, gateway, provisioningProcessRunner: runner);

        OperationResponse result = await toolkit.ProvisionInfrastructureAsync(
            "aws-eks", "small", "plan", NativeEksVariables, false, string.Empty);

        Assert.Equal("exact-plan-input-mismatch", result.Status);
        string planPath = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        Assert.False(Directory.Exists(Path.GetDirectoryName(planPath)));
        Assert.Null(result.ApprovalRequest);
    }

    [Theory]
    [InlineData("db_connection_secret_arn", "")]
    [InlineData("admin_password_secret_arn", "arn:aws:secretsmanager:us-west-2:123456789012:secret:bad*")]
    [InlineData("master_key_secret_arn", "arn:aws:secretsmanager:us-east-1:123456789012:secret:other")]
    public async Task NativeEksMissingOrMalformedGeneratedReferencesCannotBypassSecretRequirement(
        string name, string value)
    {
        using TerraformTestRoot root = new(rootName: "aws-eks-native");
        File.Delete(Path.Combine(root.AwsRoot, "terraform.tfvars"));
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(
            root.Path, ExecutionMode.Plan, ExecutionTier.Plan) with
        {
            RuntimeKind = "aws-eks", RuntimeBackend = "honua-kubernetes-deployment",
            DeployTargetId = "eks-dev", ServerTargetId = "server-eks-dev",
            TerraformDeploymentTargets = ["eks"]
        };
        HonuaOperationsToolkit toolkit = new(runtime, gateway, provisioningProcessRunner: runner);
        JsonNode variables = JsonNode.Parse(NativeEksVariables)!;
        if (value.Length == 0) variables.AsObject().Remove(name);
        else variables[name] = value;

        OperationResponse result = await toolkit.ProvisionInfrastructureAsync(
            "aws-eks", "small", "plan", variables.ToJsonString(), false, string.Empty);

        Assert.Contains(result.Status, new[] { "variables-invalid", "secret-input-required" });
        Assert.Empty(runner.Calls);
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
    public async Task LegacySecretInputPlanKeepsPlanReadyButCannotExportProtectedEvidence()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse result = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);

        Assert.Equal("terraform-plan-ready", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("host-evidence-unsupported", StringComparison.Ordinal));
        string planPath = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        string directory = Path.GetDirectoryName(planPath)!;
        Assert.False(File.Exists(Path.Combine(directory, "private-host-evidence.json")));
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            Assert.Equal(1, await PrivatePlanHostEvidence.ExportAsync(
                result.ProvisioningLineage!.ProvisioningOperationId, output, error));
            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
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
    public async Task RetainedLegacySidecarWithSecretInputHashesCannotBeExported()
    {
        if (!PrivatePlanHostEvidence.IsSupportedHost) return;
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway, provisioningProcessRunner: runner);

        OperationResponse plan = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{}", false, string.Empty);
        Assert.Equal("terraform-plan-ready", plan.Status);
        string operationId = plan.ProvisioningLineage!.ProvisioningOperationId;
        string planPath = Assert.Single(runner.Calls, call => call.Operation == "terraform-exact-plan.sh")
            .Option("--plan-out")!;
        string directory = Path.GetDirectoryName(planPath)!;
        string sidecarPath = Path.Combine(directory, "private-host-evidence.json");
        Assert.False(File.Exists(sidecarPath));
        await File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(new
        {
            VariablesBase64 = "",
            ExecutedInputEntries = new[] { $"env:password={Hash(Encoding.UTF8.GetBytes("guessable"))}" }
        }));
        File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            int result = await PrivatePlanHostEvidence.ExportAsync(operationId, output, error);
            Assert.Equal(1, result);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("private-plan-evidence-refused", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
