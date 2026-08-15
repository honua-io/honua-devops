using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GitOps;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// Opt-in live integration for the GitOps actuation spine. Gated by HONUA_DEVOPS_LIVE_INTEGRATION
// so CI never reaches a backend. The live assertion is intentionally the SAFE one: a plan-mode
// actuation against a real deploy-control target must remain non-mutating (plan-only) and must
// not create, submit, or roll back any operation. This proves the default-safe invariant holds
// against the real server, not just a stub.
public class GitOpsActuationLiveIntegrationTests
{
    [Fact]
    public async Task LiveGitOpsActuation_PlanModeStaysNonMutatingAgainstRealTarget()
    {
        if (!LiveIntegrationEnabled())
        {
            return;
        }

        OperationRuntime configured = OperationRuntime.Load();
        if (string.IsNullOrWhiteSpace(configured.DeployTargetId))
        {
            return;
        }

        // Force plan posture regardless of the ambient EXECUTION_MODE so this live test can never
        // mutate the target, even if the operator's environment is set to execute.
        OperationRuntime planRuntime = configured with { ExecutionMode = ExecutionMode.Plan };

        using BackendGateway gateway = new(BackendConfiguration.Load());
        GitOpsExecutor executor = new(planRuntime, gateway, OperatorPolicyModel.Load());

        string desiredRevision = Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_DESIRED_REVISION")?.Trim()
            is { Length: > 0 } configuredRevision
            ? configuredRevision
            : "honua-devops-live-test";

        GitOpsExecutionResult result = await executor.ExecuteSyncAsync(
            desiredRevision,
            currentRevision: null,
            reason: "honua-devops live plan-mode safety check",
            idempotencyKey: $"honua-devops:live:{desiredRevision}",
            correlationId: "honua-devops:live:sync",
            priority: "normal",
            parameters: new Dictionary<string, string>
            {
                ["service"] = "honua-devops-live-test",
                ["source"] = "honua-devops-live-test"
            },
            authorizationDryRun: true,
            policyGate: "plan-only",
            CancellationToken.None);

        Assert.Equal(GitOpsExecutionStatus.PlanOnly, result.Status);
        Assert.False(result.Mutated);
        Assert.Null(result.OperationId);
        Assert.DoesNotContain(result.BackendSteps, step => step.MutatesState);
    }

    private static bool LiveIntegrationEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
