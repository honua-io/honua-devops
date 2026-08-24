using System.Reflection;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Tests;

// The response-level actuation invariant (issue #151):
//
//   executed/applied  <=>  typed actuator + durable receipt + Mutated=true
//                     AND  a successful mutating backend step
//                     AND  terminal success from the actuator authority
//
// Models, Console, audit consumers, and release evidence reason from the top-level status,
// so a contradictory pairing must fail loudly at construction rather than become evidence.
public class ActuationResponseGuardTests
{
    [Fact]
    public void Validate_Rejects_AppliedWithoutAnActuatorResult()
    {
        // Policy configuration or caller intent is not an implemented action.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("auto-remediation-applied", result: null));

        Assert.Contains("no actuator result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_AppliedWithoutAReceipt()
    {
        ActuationResult result = Executed() with { Receipt = null };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("auto-remediation-applied", result));

        Assert.Contains("without a durable operation/action receipt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_ExecutedWithoutAMutatingBackendStep()
    {
        ActuationResult result = Executed() with
        {
            BackendSteps = [Step("deploy-plan", success: true, mutates: false)]
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("runbook-executed", result));

        Assert.Contains("without a successful mutating backend step", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_ExecutedWhenTheMutatingStepFailed()
    {
        ActuationResult result = Executed() with
        {
            BackendSteps = [Step("deploy-operation-submit", success: false, mutates: true)]
        };

        Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("runbook-executed", result));
    }

    [Fact]
    public void Validate_Rejects_ExecutedWithMutatedFalse()
    {
        ActuationResult result = Executed() with { Mutated = false };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("runbook-executed", result));

        Assert.Contains("Mutated=false", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_ReadyWithAnUnsupportedActuator()
    {
        ActuationResult result = ActuationResult.Unsupported("clear-tile-cache", "roads-api:staging", "no actuator");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("runbook-execute-ready", result));

        Assert.Contains("no typed actuator is registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_ExecutedWhenTheAuthorityReportedANonSuccess()
    {
        ActuationResult result = Executed() with { Outcome = ActuationOutcome.AwaitingApproval };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("runbook-executed", result));

        Assert.Contains("awaiting-approval", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_FailureAlongsideAMutatedSuccess()
    {
        // The inverse contradiction: the status says it failed while the authoritative result
        // carries a receipt and a successful mutating step.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActuationResponseGuard.Validate("backend-error", Executed()));

        Assert.Contains("contradicts the actuator result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Accepts_AFullyEvidencedExecution()
    {
        ActuationResponseGuard.Validate("runbook-executed", Executed());
        ActuationResponseGuard.Validate("auto-remediation-applied", Executed());
    }

    [Fact]
    public void ResolveStatus_KeepsNonSuccessOutcomesDistinct()
    {
        ActuationStatusVocabulary vocabulary = new("runbook", Executed: "runbook-executed");

        Assert.Equal("unsupported-action", Resolve(vocabulary, ActuationOutcome.UnsupportedAction));
        Assert.Equal("runbook-plan-only", Resolve(vocabulary, ActuationOutcome.PlanOnly));
        Assert.Equal("runbook-observed", Resolve(vocabulary, ActuationOutcome.Observed));
        Assert.Equal("runbook-awaiting-approval", Resolve(vocabulary, ActuationOutcome.AwaitingApproval));
        Assert.Equal("runbook-approval-required", Resolve(vocabulary, ActuationOutcome.ApprovalRequired));
        Assert.Equal("runbook-in-progress", Resolve(vocabulary, ActuationOutcome.InProgress));
        Assert.Equal("runbook-failed", Resolve(vocabulary, ActuationOutcome.Failed));
        Assert.Equal("runbook-indeterminate", Resolve(vocabulary, ActuationOutcome.Indeterminate));
        Assert.Equal("contract-unavailable", Resolve(vocabulary, ActuationOutcome.ContractUnavailable));
    }

    [Fact]
    public void ResolveMutated_ComesFromTheSameAuthoritativeResultAsTheStatus()
    {
        ActuationResult executed = Executed();
        Assert.Equal("runbook-executed", ActuationResponseGuard.ResolveStatus(
            new ActuationStatusVocabulary("runbook", Executed: "runbook-executed"),
            executed));
        Assert.True(ActuationResponseGuard.ResolveMutated(executed));

        ActuationResult refused = ActuationResult.Unsupported("x", "t", "none");
        Assert.False(ActuationResponseGuard.ResolveMutated(refused));
        Assert.False(ActuationResponseGuard.ResolveMutated(null));
    }

    // Every ready/executed/applied token in the shipped toolset must be one the guard knows
    // about, so a new claim token cannot appear without passing through this invariant.
    [Fact]
    public void EveryToolStatusTokenClaimingExecutionIsKnownToTheGuard()
    {
        IEnumerable<string> literals = typeof(HonuaOperationsToolkit).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith("Honua.DevOps.Agent.Operations", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal);

        string[] unknownClaims =
        [
            .. literals.Where(value =>
                (value.EndsWith("-executed", StringComparison.Ordinal)
                    || value.EndsWith("-applied", StringComparison.Ordinal))
                && !ActuationResponseGuard.ExecutedStatusTokens.Contains(value))
        ];

        Assert.Empty(unknownClaims);
    }

    private static string Resolve(ActuationStatusVocabulary vocabulary, string outcome)
        => ActuationResponseGuard.ResolveStatus(
            vocabulary,
            new ActuationResult(
                ActuatorId: "honua.test",
                Action: "test",
                Target: "target",
                Outcome: outcome,
                Mutated: false,
                Receipt: null,
                OperationId: null,
                BackendSteps: [],
                Findings: [],
                BlockingReasons: []));

    private static ActuationResult Executed()
        => new(
            ActuatorId: "honua.deploy-operation.submit",
            Action: "deploy-submit",
            Target: "roads-api:dev",
            Outcome: ActuationOutcome.Executed,
            Mutated: true,
            Receipt: new ActuationReceipt("honua.deploy-operation.submit", "op-1", "honua-server.deploy-control", "Succeeded"),
            OperationId: "op-1",
            BackendSteps: [Step("deploy-operation-submit", success: true, mutates: true)],
            Findings: [],
            BlockingReasons: []);

    private static OperationBackendStep Step(string name, bool success, bool mutates)
        => new(name, "http://localhost/endpoint", success, "detail", "preview", mutates);
}
