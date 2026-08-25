using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public sealed class TerraformProvisioningTests
{
    [Fact]
    public async Task Plan_UsesAllowlistedRootAndNeverStartsApply()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("Terraform has been successfully initialized!"),
            Success("Plan: 7 to add, 0 to change, 0 to destroy."),
            Success(TerraformShowOutput));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
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
        Assert.Contains("7 to add", response.Summary, StringComparison.Ordinal);
        Assert.Equal(3, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal("terraform", call.FileName));
        Assert.Equal("init", runner.Calls[0].Arguments[0]);
        Assert.Equal("plan", runner.Calls[1].Arguments[0]);
        Assert.Equal("show", runner.Calls[2].Arguments[0]);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments[0] == "apply");
        Assert.All(runner.Calls, call => Assert.Equal(root.AwsRoot, call.WorkingDirectory));

        // The caller must be able to review WHAT it is confirming, not just three numbers.
        Assert.Contains(response.Findings, finding => finding.Contains("aws_ecs_service.honua", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("aws_db_instance.honua", StringComparison.Ordinal));

        // Replacements and deletions are called out explicitly, not buried in the roster.
        string destructive = Assert.Single(
            response.Findings,
            finding => finding.StartsWith("DESTRUCTIVE changes in this plan:", StringComparison.Ordinal));
        Assert.Contains("must be replaced aws_db_instance.honua", destructive, StringComparison.Ordinal);
        Assert.Contains("will be destroyed aws_s3_bucket.stale", destructive, StringComparison.Ordinal);
        Assert.Contains(
            response.Findings,
            finding => finding.StartsWith("Redacted plan digest", StringComparison.Ordinal));
        Assert.Contains(response.BackendSteps!, step => step.Name == "terraform-show" && !step.MutatesState);

        DeleteSavedPlanFrom(runner.Calls[1]);
    }

    [Fact]
    public async Task Plan_DerivesTheDefaultNamePrefixFromTheSelectedEnvironment()
    {
        // A staging plan without an explicit name_prefix must not target `honua-dev`
        // infrastructure: that collides with (or reconciles) the existing development cell,
        // including under a break-glass destroy.
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("initialized"),
            Success("Plan: 1 to add, 0 to change, 0 to destroy."),
            Success(TerraformShowOutput));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{\"environment\":\"staging\"}",
            confirmed: false,
            confirmation: string.Empty);

        Assert.Equal("terraform-plan-ready", response.Status);
        ProcessCall plan = runner.Calls[1];
        string varFileArgument = Assert.Single(
            plan.Arguments,
            argument => argument.StartsWith("-var-file=", StringComparison.Ordinal));
        string variables = File.ReadAllText(varFileArgument["-var-file=".Length..]);

        Assert.Contains("\"name_prefix\": \"honua-staging\"", variables, StringComparison.Ordinal);
        Assert.DoesNotContain("honua-dev", variables, StringComparison.Ordinal);
        DeleteSavedPlanFrom(plan);
    }

    [Fact]
    public async Task Plan_KeepsAnExplicitNamePrefixOverTheEnvironmentDefault()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("initialized"),
            Success("Plan: 1 to add, 0 to change, 0 to destroy."),
            Success(TerraformShowOutput));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{\"environment\":\"staging\",\"name_prefix\":\"honua-cell-a\"}",
            confirmed: false,
            confirmation: string.Empty);

        Assert.Equal("terraform-plan-ready", response.Status);
        ProcessCall plan = runner.Calls[1];
        string varFileArgument = Assert.Single(
            plan.Arguments,
            argument => argument.StartsWith("-var-file=", StringComparison.Ordinal));
        string variables = File.ReadAllText(varFileArgument["-var-file=".Length..]);

        Assert.Contains("\"name_prefix\": \"honua-cell-a\"", variables, StringComparison.Ordinal);
        DeleteSavedPlanFrom(plan);
    }

    [Fact]
    public async Task Plan_RefusesWithoutStartingAnythingWhenTerraformIsNotInstalled()
    {
        // The published MCP container ships only the operator binary, so this is the
        // documented state there until Terraform and the honua-iac checkout are mounted.
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new() { TerraformAvailable = false };
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{}",
            confirmed: false,
            confirmation: string.Empty);

        Assert.Equal("terraform-unavailable", response.Status);
        Assert.Empty(runner.Calls);
        Assert.Contains(
            response.Actions,
            action => action.Contains("HONUA_DEVOPS_TERRAFORM_LOCAL_PATH=/honua-iac", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_PlansThenPassesOnlySavedPlanToApply()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("initialized"),
            Success("Plan: 3 to add, 1 to change, 0 to destroy."),
            Success(TerraformShowOutput));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit planner = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse planResponse = await planner.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{\"environment\":\"dev\"}",
            confirmed: false,
            confirmation: string.Empty);
        string challenge = ExtractChallenge(planResponse, "confirmation=");
        string approvalReceipt = CreateApprovalReceipt(planResponse, "apply");
        runner.Enqueue(Success("Apply complete! Resources: 3 added, 1 changed, 0 destroyed."));

        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "apply",
            "{\"environment\":\"dev\"}",
            confirmed: true,
            confirmation: challenge,
            approvalReceiptJson: approvalReceipt);

        Assert.Equal("infrastructure-provisioned", response.Status);
        Assert.Equal(4, runner.Calls.Count);
        ProcessCall plan = runner.Calls[1];
        ProcessCall apply = runner.Calls[3];
        Assert.Equal("show", runner.Calls[2].Arguments[0]);
        string outArgument = Assert.Single(plan.Arguments, argument => argument.StartsWith("-out=", StringComparison.Ordinal));
        string savedPlan = outArgument["-out=".Length..];
        Assert.Equal(["apply", "-input=false", "-no-color", "-auto-approve", savedPlan], apply.Arguments);
        Assert.DoesNotContain(apply.Arguments, argument => argument.StartsWith("-var", StringComparison.Ordinal));
        Assert.True(Assert.Single(response.BackendSteps!, step => step.Name == "terraform-apply").MutatesState);
    }

    [Fact]
    public async Task Apply_AtomicallyClaimsSavedPlanAndRefusesConcurrentReuse()
    {
        using TerraformTestRoot root = new();
        BlockingApplyProvisioningProcessRunner runner = new();
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit planner = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse planResponse = await planner.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{\"environment\":\"dev\"}",
            confirmed: false,
            confirmation: string.Empty);
        string challenge = ExtractChallenge(planResponse, "confirmation=");
        string approvalReceipt = CreateApprovalReceipt(planResponse, "apply");

        HonuaOperationsToolkit executor = new(
            CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy(),
            provisioningProcessRunner: runner);
        Task<OperationResponse> firstApply = executor.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "apply",
            "{\"environment\":\"dev\"}",
            confirmed: true,
            confirmation: challenge,
            approvalReceiptJson: approvalReceipt);
        await runner.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        OperationResponse concurrentApply = await executor.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "apply",
            "{\"environment\":\"dev\"}",
            confirmed: true,
            confirmation: challenge,
            approvalReceiptJson: approvalReceipt);

        Assert.Equal("confirmation-required", concurrentApply.Status);
        Assert.Contains("already been claimed", concurrentApply.Summary, StringComparison.Ordinal);
        Assert.Equal(1, runner.ApplyCalls);

        runner.AllowApply.TrySetResult(true);
        OperationResponse firstResponse = await firstApply;
        Assert.Equal("infrastructure-provisioned", firstResponse.Status);
        Assert.Equal(1, runner.ApplyCalls);
    }

    [Fact]
    public async Task Apply_RefusesBeforeProcessStartWithoutTrustedPlanBoundApprovalReceipt()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("initialized"),
            Success("Plan: 1 to add, 0 to change, 0 to destroy."),
            Success(TerraformShowOutput));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit planner = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ExtractChallenge(plan, "confirmation=");

        HonuaOperationsToolkit executor = new(
            CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy(),
            provisioningProcessRunner: runner);
        OperationResponse missing = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge, string.Empty);
        OperationResponse substituted = await executor.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge,
            CreateApprovalReceipt(plan, "destroy"));

        Assert.Equal("confirmation-required", missing.Status);
        Assert.Contains("signed honua.devops.provision-approval/v1", missing.Summary, StringComparison.Ordinal);
        Assert.Equal("confirmation-required", substituted.Status);
        Assert.Contains("exact operation", substituted.Summary, StringComparison.Ordinal);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task PlanFailure_NeverStartsApply()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("initialized"),
            Failure("Error: invalid provider configuration"));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{}",
            confirmed: false,
            confirmation: string.Empty);

        Assert.Equal("terraform-plan-failed", response.Status);
        Assert.Equal(2, runner.Calls.Count);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments[0] == "apply");
    }

    [Fact]
    public async Task Destroy_RequiresBreakGlassBeforeStartingTerraform()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new();
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy(),
            provisioningProcessRunner: runner);

        OperationResponse response = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "destroy",
            "{}",
            confirmed: true,
            confirmation: "destroy:aws-ecs:dev");

        Assert.Equal("break-glass-required", response.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Variables_RejectSecretsAndUnknownPathsBeforeProcessStart()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new();
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);

        OperationResponse secret = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "plan",
            "{\"honua_admin_password\":\"do-not-log\"}",
            false,
            string.Empty);
        OperationResponse stack = await toolkit.ProvisionInfrastructureAsync(
            "../../other",
            "small",
            "plan",
            "{}",
            false,
            string.Empty);

        Assert.Equal("variables-invalid", secret.Status);
        Assert.Equal("unsupported-stack", stack.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal("<redacted>", Redaction.ScrubValue("variablesJson", "{\"safe\":\"still-private\"}"));
    }

    [Fact]
    public async Task InstallHandoff_WritesReferenceOnlyProxyContract()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("{\"honua_url\":{\"sensitive\":false,\"type\":\"string\",\"value\":\"https://honua.example.test\"}}"));
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        string outputDirectory = System.IO.Path.Combine(root.Path, "handoff");
        const string secretRef = "arn:aws:secretsmanager:us-west-2:123456789012:secret:honua-admin";

        OperationResponse response = await toolkit.InstallHandoffAsync(
            "aws-ecs",
            string.Empty,
            secretRef,
            outputDirectory,
            overwrite: false,
            provisioningOperationId: "urn:honua:provisioning:test-handoff");

        Assert.Equal("install-handoff-written", response.Status);
        string proxyConfig = await File.ReadAllTextAsync(System.IO.Path.Combine(outputDirectory, "honua-mcp-proxy.handoff.json"));
        string envExample = await File.ReadAllTextAsync(System.IO.Path.Combine(outputDirectory, "honua.env.example"));
        Assert.Contains("https://honua.example.test", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("https://honua.example.test/mcp", proxyConfig, StringComparison.Ordinal);
        Assert.Contains(secretRef, proxyConfig, StringComparison.Ordinal);
        Assert.Contains("@honua/mcp-server@2026.1.1", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("sha512-", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"admin\"", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("honua_admin_server_status", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"analysis\"", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("honua_buffer_features", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("honua_export_dataset", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"esri-gp\"", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("honua_esri_gp_list_tasks", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("honua_esri_gp_describe_task", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("honua_esri_gp_execute_task", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("\"failClosed\": true", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("HONUA_ADMIN_KEY is intentionally absent", envExample, StringComparison.Ordinal);
        Assert.Contains("Required server capabilities: admin", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log", proxyConfig, StringComparison.Ordinal);
        Assert.Single(runner.Calls);
        Assert.Equal(["output", "-json"], runner.Calls[0].Arguments);
    }

    private static string ExtractChallenge(OperationResponse response, string marker)
    {
        string action = Assert.Single(response.Actions, value => value.Contains(marker, StringComparison.Ordinal));
        int start = action.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = action.IndexOf('.', start);
        return (end < 0 ? action[start..] : action[start..end]).Trim('`', ' ');
    }

    private static void DeleteSavedPlanFrom(ProcessCall planCall)
    {
        string outArgument = Assert.Single(planCall.Arguments, argument => argument.StartsWith("-out=", StringComparison.Ordinal));
        string? directory = System.IO.Path.GetDirectoryName(outArgument["-out=".Length..]);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OperationRuntime CreateRuntime(string terraformRoot, ExecutionMode mode, ExecutionTier tier)
    {
        return new OperationRuntime(
            mode,
            tier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "trunk",
            TerraformLocalPath: terraformRoot,
            TerraformDeploymentTargets: ["ecs"],
            ProductionEnvironments: ["prod"],
            ProvisionApprovalIssuerKeys: new Dictionary<string, string>
            {
                [ApprovalIssuer] = Convert.ToBase64String(ApprovalKey)
            },
            McpProxyPackage: "@honua/mcp-server@2026.1.1",
            McpProxyIntegrity: "sha512-dGVzdC1pbnRlZ3JpdHk=",
            CandidateReference: "honua-2026.1.1-test");
    }

    private const string ApprovalIssuer = "test://release-approver";
    private static readonly byte[] ApprovalKey = SHA256.HashData(Encoding.UTF8.GetBytes("honua-devops-test-approval-key"));

    private static string CreateApprovalReceipt(OperationResponse plan, string action)
    {
        ProvisioningLineage lineage = Assert.IsType<ProvisioningLineage>(plan.ProvisioningLineage);
        DateTimeOffset issued = DateTimeOffset.UtcNow.AddSeconds(-1);
        DateTimeOffset expires = issued.AddMinutes(15);
        string keyId = Convert.ToHexString(SHA256.HashData(ApprovalKey)).ToLowerInvariant()[..16];
        string receiptId = $"approval-{Guid.NewGuid():n}";
        string canonical = string.Join('\n',
            "honua.devops.provision-approval/v1",
            receiptId,
            ApprovalIssuer,
            keyId,
            lineage.ProvisioningOperationId,
            lineage.PlanSha256!,
            action,
            "aws-ecs",
            "dev",
            "approved",
            issued.ToUniversalTime().ToString("O"),
            expires.ToUniversalTime().ToString("O"));
        string signature = Convert.ToBase64String(HMACSHA256.HashData(ApprovalKey, Encoding.UTF8.GetBytes(canonical)));
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "honua.devops.provision-approval/v1",
            approvalReceiptId = receiptId,
            issuer = ApprovalIssuer,
            keyId,
            provisioningOperationId = lineage.ProvisioningOperationId,
            planSha256 = lineage.PlanSha256,
            action,
            stack = "aws-ecs",
            environment = "dev",
            decision = "approved",
            issuedAtUtc = issued,
            expiresAtUtc = expires,
            signature
        });
    }

    private static BackendGateway CreateGateway()
    {
        HttpClient client = new(new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        return new BackendGateway(
            new BackendConfiguration(
                new Uri("http://localhost:8080"),
                new Uri("http://localhost:4318"),
                null,
                null,
                "healthz/ready",
                "health",
                "v1/logs/search",
                "v1/metrics/search",
                "api/v1/admin/observability/errors",
                "api/v1/admin/observability/telemetry",
                "api/v1/metrics/health",
                "api/v1/metrics/performance",
                "api/v1/metrics/database",
                "api/v1/metrics/cache",
                "api/v1/metrics/memory",
                "api/v1/admin/performance/database/query-cache/statistics",
                "api/v1/admin/version",
                "api/v1/admin/capabilities",
                "api/v1/admin/manifest",
                "api/v1/admin/manifest/apply",
                TimeSpan.FromSeconds(5)),
            client);
    }

    private static OperatorPolicyModel DirectAllowedPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }

    private static ProvisioningProcessResult Success(string output)
        => new(0, output, string.Empty, false);

    // A representative `terraform show` rendering of a saved plan: one create, one
    // replacement, and one deletion, so the reviewable-evidence assertions cover the
    // destructive cases as well as the benign one.
    private const string TerraformShowOutput = """
Terraform will perform the following actions:

  # aws_ecs_service.honua will be created
  + resource "aws_ecs_service" "honua" {
    }

  # aws_db_instance.honua must be replaced
  -/+ resource "aws_db_instance" "honua" {
    }

  # aws_s3_bucket.stale will be destroyed
  - resource "aws_s3_bucket" "stale" {
    }

Plan: 7 to add, 0 to change, 0 to destroy.
""";

    private static ProvisioningProcessResult Failure(string error)
        => new(1, string.Empty, error, false);

    private sealed class FakeProvisioningProcessRunner(params ProvisioningProcessResult[] results) : IProvisioningProcessRunner
    {
        private readonly Queue<ProvisioningProcessResult> _results = new(results);

        internal List<ProcessCall> Calls { get; } = [];

        internal void Enqueue(ProvisioningProcessResult result) => _results.Enqueue(result);

        // The fake launcher can always "run" what it is asked for; availability of the real
        // terraform binary is covered by its own tests.
        internal bool TerraformAvailable { get; set; } = true;

        public bool CanRun(string fileName)
            => !string.Equals(fileName, "terraform", StringComparison.Ordinal) || TerraformAvailable;

        public Task<ProvisioningProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new ProcessCall(fileName, [.. arguments], workingDirectory));
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("The fake process runner has no queued result.");
            }
            ProvisioningProcessResult result = _results.Dequeue();
            if (result.Succeeded && arguments.Count > 0 && arguments[0] == "plan")
            {
                string? outputArgument = arguments.FirstOrDefault(argument => argument.StartsWith("-out=", StringComparison.Ordinal));
                if (outputArgument is not null)
                {
                    File.WriteAllText(outputArgument["-out=".Length..], "fake saved terraform plan");
                }
            }
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingApplyProvisioningProcessRunner : IProvisioningProcessRunner
    {
        internal TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> AllowApply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ApplyCalls { get; private set; }

        public bool CanRun(string fileName)
        {
            _ = fileName;
            return true;
        }

        public async Task<ProvisioningProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _ = fileName;
            _ = workingDirectory;
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            string command = arguments[0];
            if (command == "init")
            {
                return Success("initialized");
            }
            if (command == "plan")
            {
                string outputArgument = Assert.Single(arguments, argument => argument.StartsWith("-out=", StringComparison.Ordinal));
                File.WriteAllText(outputArgument["-out=".Length..], "fake saved terraform plan");
                return Success("Plan: 1 to add, 0 to change, 0 to destroy.");
            }
            if (command == "show")
            {
                return Success("  # aws_ecs_service.honua will be created");
            }
            if (command == "apply")
            {
                ApplyCalls++;
                ApplyStarted.TrySetResult(true);
                await AllowApply.Task.WaitAsync(cancellationToken);
                return Success("Apply complete! Resources: 1 added, 0 changed, 0 destroyed.");
            }

            throw new InvalidOperationException($"Unexpected fake Terraform command `{command}`.");
        }
    }

    private sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

    private sealed class TerraformTestRoot : IDisposable
    {
        internal TerraformTestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"honua-devops-provision-tests-{Guid.NewGuid():n}");
            AwsRoot = System.IO.Path.Combine(Path, "infrastructure", "terraform", "examples", "aws");
            Directory.CreateDirectory(AwsRoot);
            File.WriteAllText(System.IO.Path.Combine(AwsRoot, "main.tf"), "module \"honua\" {}\n");
            File.WriteAllText(System.IO.Path.Combine(AwsRoot, "variables.tf"), "variable \"honua_admin_password\" { sensitive = true }\n");
            File.WriteAllText(System.IO.Path.Combine(AwsRoot, "terraform.tfvars"), "# fixture: real secrets are never used\n");
        }

        internal string Path { get; }

        internal string AwsRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
