using System.Net;
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
            Success("Plan: 7 to add, 0 to change, 0 to destroy."));
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
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal("terraform", call.FileName));
        Assert.Equal("init", runner.Calls[0].Arguments[0]);
        Assert.Equal("plan", runner.Calls[1].Arguments[0]);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments[0] == "apply");
        Assert.All(runner.Calls, call => Assert.Equal(root.AwsRoot, call.WorkingDirectory));
        DeleteSavedPlanFrom(runner.Calls[1]);
    }

    [Fact]
    public async Task Apply_PlansThenPassesOnlySavedPlanToApply()
    {
        using TerraformTestRoot root = new();
        FakeProvisioningProcessRunner runner = new(
            Success("initialized"),
            Success("Plan: 3 to add, 1 to change, 0 to destroy."));
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
            confirmation: challenge);

        Assert.Equal("infrastructure-provisioned", response.Status);
        Assert.Equal(3, runner.Calls.Count);
        ProcessCall plan = runner.Calls[1];
        ProcessCall apply = runner.Calls[2];
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
            confirmation: challenge);
        await runner.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        OperationResponse concurrentApply = await executor.ProvisionInfrastructureAsync(
            "aws-ecs",
            "small",
            "apply",
            "{\"environment\":\"dev\"}",
            confirmed: true,
            confirmation: challenge);

        Assert.Equal("confirmation-required", concurrentApply.Status);
        Assert.Contains("already been claimed", concurrentApply.Summary, StringComparison.Ordinal);
        Assert.Equal(1, runner.ApplyCalls);

        runner.AllowApply.TrySetResult(true);
        OperationResponse firstResponse = await firstApply;
        Assert.Equal("infrastructure-provisioned", firstResponse.Status);
        Assert.Equal(1, runner.ApplyCalls);
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
            overwrite: false);

        Assert.Equal("install-handoff-ready", response.Status);
        string proxyConfig = await File.ReadAllTextAsync(System.IO.Path.Combine(outputDirectory, "honua-mcp-proxy.handoff.json"));
        string envExample = await File.ReadAllTextAsync(System.IO.Path.Combine(outputDirectory, "honua.env.example"));
        Assert.Contains("https://honua.example.test", proxyConfig, StringComparison.Ordinal);
        Assert.Contains("https://honua.example.test/mcp", proxyConfig, StringComparison.Ordinal);
        Assert.Contains(secretRef, proxyConfig, StringComparison.Ordinal);
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
            ProductionEnvironments: ["prod"]);
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

    private static ProvisioningProcessResult Failure(string error)
        => new(1, string.Empty, error, false);

    private sealed class FakeProvisioningProcessRunner(params ProvisioningProcessResult[] results) : IProvisioningProcessRunner
    {
        private readonly Queue<ProvisioningProcessResult> _results = new(results);

        internal List<ProcessCall> Calls { get; } = [];

        internal void Enqueue(ProvisioningProcessResult result) => _results.Enqueue(result);

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
