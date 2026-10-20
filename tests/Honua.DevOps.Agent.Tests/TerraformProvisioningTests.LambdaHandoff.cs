using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Theory]
    [InlineData("allowed")]
    [InlineData("revoked")]
    [InlineData("timeout")]
    [InlineData("tenant-changed")]
    [InlineData("alias-changed")]
    [InlineData("route-changed")]
    [InlineData("install-authorization-timeout")]
    [InlineData("verify-authorization-timeout")]
    public async Task LambdaActualProducerShapeFlowsThroughReviewedApplyAndRechecksAuthorityAfterPublicProbes(string outcome)
    {
        using var root = new TerraformTestRoot(rootName: "aws-serverless");
        var outputsJson = ProvisioningSubstrateFixtures.ReadFixture("documents/lambda-qualified-producer-output.json");
        using var outputs = JsonDocument.Parse(outputsJson);
        var projection = outputs.RootElement.GetProperty("runtime_binding_contract").GetProperty("value");
        var targetId = projection.GetProperty("targetId").GetString()!;
        Assert.True(ProvisionedRuntimeFacts.TryReadOutputs(outputsJson, ProvisioningRuntimeProfile.Resolve("aws-lambda"), targetId, "dev", out var facts, out var error), error);
        var deployment = outputs.RootElement.GetProperty("deployment_contract").GetProperty("value");
        var endpoint = deployment.GetProperty("endpoints").GetProperty("public_base_url").GetString()!;
        var variables = JsonSerializer.Serialize(new
        {
            region = facts!.Region, name_prefix = "honua-contract", environment = "dev",
            honua_image_uri = deployment.GetProperty("identity").GetProperty("image_reference").GetString()
        });
        var provider = new AwsLambdaRuntimeObservationTests.Reader("valid", facts, endpoint);
        var runner = new LambdaHandoffRunner(new FakeSubstrateRunner
        {
            TerraformOutputJson = outputsJson,
            ExactPlanMetadataJson = ProvisioningSubstrateFixtures.ReadFixture("documents/lambda-exact-plan-metadata.json"),
            ExecReceiptJson = ProvisioningSubstrateFixtures.ReadFixture("documents/lambda-exec-receipt.json")
        }, provider);
        var publicProbesFinished = false;
        var authorizationCalls = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            Assert.EndsWith("/api/v1/agent/devops/authorize", request.RequestUri!.AbsolutePath);
            authorizationCalls++;
            if (authorizationCalls == 1 && outcome == "install-authorization-timeout"
                || authorizationCalls == 2 && outcome == "verify-authorization-timeout")
                throw new TaskCanceledException("Bounded initial canonical authorization timed out.");
            if (publicProbesFinished && outcome == "timeout")
                throw new TaskCanceledException("Bounded canonical authorization timed out.");
            if (publicProbesFinished && outcome == "revoked")
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    targetId = "local", deployTargetId = targetId, environment = "dev", runtimeKind = "aws-lambda",
                    backend = "honua-gitops-aws-lambda", tenantId = publicProbesFinished && outcome == "tenant-changed" ? "tenant-b" : "tenant-a",
                    targetName = facts.ResourceIdentity.GetProperty("functionArn").GetString(),
                    resourceParameters = new Dictionary<string, string> { ["aws.region"] = facts.Region },
                    usesCustomProviderEndpoint = false
                }))
            };
        });
        using var gateway = ProvisioningSubstrateFixtures.CreateGateway(handler);
        var runtime = LambdaRuntime(root.Path) with { DeployTargetId = targetId };
        var planner = new HonuaOperationsToolkit(runtime, gateway, provisioningProcessRunner: runner);
        var plan = await planner.ProvisionInfrastructureAsync("aws-lambda", "small", "plan", variables, false, "");
        Assert.Equal("terraform-plan-ready", plan.Status);
        var verifier = new CallbackInstallVerifier(() =>
        {
            publicProbesFinished = true;
            provider.ScenarioOverride = outcome switch
            {
                "alias-changed" => "wrong-version",
                "route-changed" => "wrong-route",
                _ => null
            };
        });
        var toolkit = new HonuaOperationsToolkit(runtime with { ExecutionMode = ExecutionMode.Execute, ExecutionTier = ExecutionTier.ExecuteLowerEnv },
            gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner, installHandoffVerifier: verifier);
        var apply = await toolkit.ProvisionInfrastructureAsync("aws-lambda", "small", "apply", variables, true,
            ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation="),
            ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply", stack: "aws-lambda"));
        Assert.True(apply.Status == "infrastructure-provisioned", apply.Status + ": " + apply.Summary);
        var directory = Path.Combine(root.Path, "lambda-handoff");
        var handoff = await toolkit.InstallHandoffAsync("aws-lambda", "", "", directory, false, apply.ProvisioningLineage!.ProvisioningOperationId);
        if (outcome == "install-authorization-timeout")
        {
            Assert.Equal("registered-runtime-unverified", handoff.Status);
            Assert.False(File.Exists(Path.Combine(directory, "honua-mcp-proxy.handoff.json")));
            Assert.False(publicProbesFinished);
            return;
        }
        Assert.Equal("install-handoff-written", handoff.Status);
        var result = await toolkit.VerifyInstallHandoffAsync(Path.Combine(directory, "honua-mcp-proxy.handoff.json"), false);
        if (outcome == "verify-authorization-timeout")
        {
            Assert.Equal("registered-runtime-unverified", result.Status);
            Assert.False(File.Exists(Path.Combine(directory, "honua-devops-provision-binding.v2.json")));
            Assert.False(publicProbesFinished);
            return;
        }
        Assert.True(publicProbesFinished);
        var providerChanged = outcome is "alias-changed" or "route-changed";
        Assert.Equal(providerChanged ? 3 : 4, authorizationCalls); // final authority follows the second provider observation
        Assert.Equal(2, provider.Commands.Count(command => command.Contains("get-alias")));
        var bindingPath = Path.Combine(directory, "honua-devops-provision-binding.v2.json");
        if (outcome == "allowed")
        {
            Assert.Equal("install-handoff-verified", result.Status);
            var binding = await File.ReadAllTextAsync(bindingPath);
            Assert.Empty(ProvisioningContracts.ValidateTargetAwareProvisionBinding(binding));
            Assert.Equal("tenant-a", JsonNode.Parse(binding)!["serverTarget"]!["tenantId"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(providerChanged ? "runtime-observation-failed"
                : outcome is "revoked" or "timeout" ? "registered-runtime-unverified" : "registered-runtime-changed", result.Status);
            Assert.False(File.Exists(bindingPath));
            Assert.False(File.Exists(Path.Combine(directory, "honua-install-verification.receipt.json")));
        }
    }

    private sealed class CallbackInstallVerifier(Action afterProbes) : IInstallHandoffVerifier
    {
        public async Task<InstallHandoffVerificationResult> VerifyAsync(InstallHandoffVerificationRequest request, CancellationToken cancellationToken = default)
        {
            var result = await new FakeInstallHandoffVerifier(true).VerifyAsync(request, cancellationToken);
            afterProbes();
            return result;
        }
    }

    private sealed class LambdaHandoffRunner(FakeSubstrateRunner terraform, IProvisioningProcessRunner aws) : IProvisioningProcessRunner
    {
        public bool CanRun(string fileName) => fileName == "aws" ? aws.CanRun(fileName) : terraform.CanRun(fileName);
        public Task<ProvisioningProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
            => (fileName == "aws" ? aws : terraform).RunAsync(fileName, arguments, workingDirectory, timeout, environment, cancellationToken);
    }
}
