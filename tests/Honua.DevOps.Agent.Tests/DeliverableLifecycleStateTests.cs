using Honua.DevOps.Agent.Operations.Deliverable;

namespace Honua.DevOps.Agent.Tests;

public class DeliverableLifecycleStateTests
{
    [Fact]
    public void ToConfigValue_AndParse_RoundTripAllStates()
    {
        foreach (DeliverableLifecycleState state in Enum.GetValues<DeliverableLifecycleState>())
        {
            string token = state.ToConfigValue();
            Assert.True(DeliverableLifecycleStateExtensions.TryParse(token, out DeliverableLifecycleState parsed));
            Assert.Equal(state, parsed);
        }
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("preview")]
    [InlineData("approved")]
    [InlineData("published")]
    public void TryParse_KnownTokensSucceed(string token)
    {
        Assert.True(DeliverableLifecycleStateExtensions.TryParse(token, out DeliverableLifecycleState parsed));
        Assert.Equal(token, parsed.ToConfigValue());
    }

    [Theory]
    [InlineData("  Approved  ")]
    [InlineData("PUBLISHED")]
    public void TryParse_IsCaseAndWhitespaceTolerant(string value)
    {
        Assert.True(DeliverableLifecycleStateExtensions.TryParse(value, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("retired")]
    public void TryParse_RejectsUnknownTokens(string? value)
    {
        Assert.False(DeliverableLifecycleStateExtensions.TryParse(value, out DeliverableLifecycleState parsed));
        Assert.Equal(DeliverableLifecycleState.Draft, parsed);
    }

    [Fact]
    public void ParseOrDraft_FallsBackToDraft()
    {
        Assert.Equal(DeliverableLifecycleState.Draft, DeliverableLifecycleStateExtensions.ParseOrDraft("nonsense"));
        Assert.Equal(DeliverableLifecycleState.Approved, DeliverableLifecycleStateExtensions.ParseOrDraft("approved"));
    }
}
