using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.Observability;

namespace Honua.DevOps.Agent.Tests;

public sealed class OpsLoopAuditTests
{
    [Fact]
    public async Task EmitAsync_AppendOrFlushFailure_IsNotDowngradedToSuccess()
    {
        IOException expected = new("disk full");
        FailingAuditSink sink = new(expected);

        IOException actual = await Assert.ThrowsAsync<IOException>(() => ToolCallAuditor.EmitAsync(
            new AuditContext("session", "execute", "execute-lower-env", "direct-allowed", "mcp", sink),
            new ToolCallRecord("mutating-tool", new Dictionary<string, object?>()),
            new { Status = "succeeded" },
            CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(1, sink.WriteCount);
    }

    [Theory]
    [InlineData("ProposalCreated")]
    [InlineData("Executed")]
    [InlineData("Failed")]
    [InlineData("RolledBack")]
    [InlineData("Indeterminate")]
    [InlineData("Canceled")]
    public async Task EmitAsync_MutatingGatewayOutcome_RecordsMutationAndBoundedSummary(string gatewayStatus)
    {
        CapturingAuditSink sink = new();
        OpsLoopReport report = new(
            Status: "proposal-created",
            ObservabilitySource: "honua-server-mcp",
            OverallHealth: "Degraded",
            PlatformReleaseVersion: null,
            PlatformReleaseCoVersioned: null,
            PlatformReleaseSkewedIds: [],
            SupportedKindsVerified: true,
            SupportedKinds: ["Deploy"],
            Findings:
            [
                new OpsLoopFindingReport(
                    FindingId: "finding-1",
                    Rule: "deploy-stuck",
                    Severity: "Critical",
                    Title: "Deploy stuck",
                    Explanation: "The deploy needs an operator decision.",
                    DetectedAt: "2026-07-10T00:00:00Z",
                    TargetId: "prod-api",
                    OperationId: "op-1",
                    ReleaseVersion: null,
                    EvidenceRefs: ["deploy-operation:op-1"],
                    RecommendedAction: new OpsLoopRecommendedAction(
                        "Deploy",
                        "Deploy prior revision",
                        "Recover service",
                        false,
                        1,
                        true),
                    RelatedAlertIds: [],
                    RelatedEventIds: [],
                    RelatedDeployOperationIds: ["op-1"],
                    Proposal: new OpsLoopProposal(
                        "finding-1",
                        gatewayStatus,
                        "proposal-1",
                        null,
                        "Awaiting approval."))
            ],
            AlertHistory: [],
            OperateTimeline: [],
            DeployOperations: [],
            McpToolsUsed: ["honua_ops_health", "honua_ops_findings"],
            EvidencePosture: new OpsLoopEvidencePosture(
                "complete-fresh",
                "2026-07-10T00:00:00.0000000+00:00",
                [],
                null),
            Bounds: new OpsLoopBounds(25, 24, 50, 12, 2048, false),
            Limitations: []);

        await ToolCallAuditor.EmitAsync(
            new AuditContext("session", "plan", "propose", "pr-first", "mcp", sink),
            new ToolCallRecord("honua_observe_diagnose_propose", new Dictionary<string, object?>()),
            report,
            CancellationToken.None);

        AuditRecord record = Assert.Single(sink.Records);
        Assert.Equal("proposal-created", record.Status);
        Assert.True(record.Mutated);
        Assert.Equal("Honua MCP ops loop: health=Degraded, evidence=complete-fresh, findings=1, proposals=1.", record.Summary);
    }

    private sealed class CapturingAuditSink : IAuditSink
    {
        internal List<AuditRecord> Records { get; } = [];

        public string Target => "capture";

        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingAuditSink(Exception failure) : IAuditSink
    {
        internal int WriteCount { get; private set; }
        public string Target => "fault-injected";
        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.FromException(failure);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
