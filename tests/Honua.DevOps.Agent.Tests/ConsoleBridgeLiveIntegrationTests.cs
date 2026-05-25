using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Tests;

// Blocked live integration coverage for the Console bridge.
//
// Per the issue #59 design and the Console Patterns Charter section 11, the bridge must
// be asserted against a real honua-server (started via Testcontainers / a live endpoint),
// never a standing in-memory mock. The durable proposal/operation/brief contracts this
// suite needs are owned by the honua-server #59/#58 child tickets and have not landed yet,
// so these tests stay gated behind HONUA_DEVOPS_LIVE_INTEGRATION and a configured deploy
// target. They become active automatically once a live server exposes the contract; until
// then they no-op rather than introduce a mock bridge into the merged build.
public class ConsoleBridgeLiveIntegrationTests
{
    [Fact]
    public async Task LiveBridge_ProposalOperationIdRoundTripsThroughStatus()
    {
        if (!LiveIntegrationEnabled())
        {
            return;
        }

        OperationRuntime runtime = OperationRuntime.Load();
        if (string.IsNullOrWhiteSpace(runtime.DeployTargetId))
        {
            // No durable target configured: the bridge would correctly stay blocked, which
            // is covered by the unit suite. Skip the live round-trip.
            return;
        }

        using BackendGateway gateway = new(BackendConfiguration.Load());
        ConsoleOperationBridge bridge = new(runtime, gateway);

        string? revision = Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_DESIRED_REVISION")?.Trim();
        if (string.IsNullOrWhiteSpace(revision))
        {
            revision = "honua-devops-live-test";
        }

        OperationResponse proposalResponse = await bridge.CreateGitOpsProposalAsync(
            service: "honua-server",
            environmentsCsv: runtime.AllowedEnvironments[0],
            revision: revision,
            action: "plan",
            changeSummary: "console bridge live integration",
            owner: "honua-devops-live-test",
            cancellationToken: CancellationToken.None);

        GitOpsProposalBridge? proposal = proposalResponse.ConsoleBridge?.Proposal;
        Assert.NotNull(proposal);
        Assert.Equal("proposed", proposal!.Status);
        Assert.False(string.IsNullOrWhiteSpace(proposal.OperationId));

        OperationResponse statusResponse = await bridge.GetDevOpsOperationStatusAsync(
            proposal.OperationId!,
            CancellationToken.None);

        DevOpsOperationStatus? status = statusResponse.ConsoleBridge?.OperationStatus;
        Assert.NotNull(status);
        // The Console-facing workflow id is stable from proposal creation through status.
        Assert.Equal(proposal.OperationId, status!.OperationId);
    }

    private static bool LiveIntegrationEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }
}
