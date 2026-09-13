using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// The outcome-oriented rollout journey the release contract promises (issue #191): "change
// preview -> scoped approval -> Checking update / Updating / Confirming service health ->
// Update complete or Previous version restored or Needs attention." A pure projection of
// state already on the operation -- no Git/PR/telemetry mechanics required to read it.
public class RolloutJourneyStatusTests
{
    [Theory]
    [InlineData(GitOpsExecutionStatus.PlanOnly)]
    [InlineData(GitOpsExecutionStatus.ExperimentalDisabled)]
    [InlineData(GitOpsExecutionStatus.ContractUnavailable)]
    [InlineData(GitOpsExecutionStatus.AwaitingApproval)]
    public void Describe_PreActuationStatuses_MapToCheckingUpdate(string status)
    {
        Assert.Equal(
            RolloutJourneyStatus.CheckingUpdate,
            RolloutJourneyStatus.Describe(status, serverStatus: null, currentPhase: null));
    }

    [Fact]
    public void Describe_Succeeded_MapsToUpdateComplete()
    {
        Assert.Equal(
            RolloutJourneyStatus.UpdateComplete,
            RolloutJourneyStatus.Describe(GitOpsExecutionStatus.Succeeded, "Succeeded", null));
    }

    [Fact]
    public void Describe_RolledBack_MapsToPreviousVersionRestored()
    {
        Assert.Equal(
            RolloutJourneyStatus.PreviousVersionRestored,
            RolloutJourneyStatus.Describe(GitOpsExecutionStatus.RolledBack, "RolledBack", null));
    }

    [Theory]
    [InlineData(GitOpsExecutionStatus.Failed)]
    [InlineData(GitOpsExecutionStatus.ApprovalRequired)]
    [InlineData(GitOpsExecutionStatus.Indeterminate)]
    public void Describe_FailureStatuses_MapToNeedsAttention(string status)
    {
        Assert.Equal(
            RolloutJourneyStatus.NeedsAttention,
            RolloutJourneyStatus.Describe(status, serverStatus: null, currentPhase: null));
    }

    [Fact]
    public void Describe_InProgress_WithNoHealthPhase_MapsToUpdating()
    {
        Assert.Equal(
            RolloutJourneyStatus.Updating,
            RolloutJourneyStatus.Describe(GitOpsExecutionStatus.InProgress, "Reconciling", currentPhase: "Applying"));
    }

    [Theory]
    [InlineData("Smoke")]
    [InlineData("VerifyHealth")]
    [InlineData("ObservationWindow")]
    [InlineData("ConfirmingRollout")]
    public void Describe_InProgress_WithHealthVerificationPhase_MapsToConfirmingServiceHealth(string phase)
    {
        Assert.Equal(
            RolloutJourneyStatus.ConfirmingServiceHealth,
            RolloutJourneyStatus.Describe(GitOpsExecutionStatus.InProgress, "InProgress", currentPhase: phase));
    }

    [Fact]
    public void Describe_InProgress_ManualInterventionRequired_MapsToNeedsAttention()
    {
        Assert.Equal(
            RolloutJourneyStatus.NeedsAttention,
            RolloutJourneyStatus.Describe(GitOpsExecutionStatus.InProgress, "ManualInterventionRequired", currentPhase: null));
    }

    [Fact]
    public void From_ProjectsFromAnExecutionResult()
    {
        GitOpsExecutionResult result = new(
            Status: GitOpsExecutionStatus.RolledBack,
            OperationId: "op-1",
            ServerStatus: "RolledBack",
            Mutated: true,
            Decision: GitOpsActuationDecision.PlanOnly("direct-allowed", "gate", "why"),
            BackendSteps: [],
            Findings: [],
            BlockingReasons: []);

        Assert.Equal(RolloutJourneyStatus.PreviousVersionRestored, RolloutJourneyStatus.From(result));
    }
}
