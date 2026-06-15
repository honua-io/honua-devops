using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.WorkIntake;

namespace Honua.DevOps.Agent.Tests;

public class WorkIntakeEditionGateTests
{
    [Theory]
    [InlineData("community")]
    [InlineData("pro")]
    [InlineData("professional")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_RefusesBelowEnterprise(string? edition)
    {
        Assert.False(WorkIntakeEditionGate.IsAllowed(edition));

        OperationResponse refusal = WorkIntakeEditionGate.BuildRefusal(edition);
        Assert.Equal("edition-gated", refusal.Status);
        Assert.Contains("enterprise", refusal.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enterprise")]
    [InlineData("Enterprise")]
    [InlineData("ENTERPRISE")]
    public void IsAllowed_AllowsAtEnterprise(string edition)
    {
        Assert.True(WorkIntakeEditionGate.IsAllowed(edition));
    }
}
