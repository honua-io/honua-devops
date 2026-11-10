using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Tests;

public sealed class ToolCallAuditFailureTests
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

    [Fact]
    public async Task EmitAsync_SerializedToolResult_ParsesMutationAcknowledgement()
    {
        CapturingAuditSink sink = new();

        await ToolCallAuditor.EmitAsync(
            new AuditContext("session", "plan", "propose", "pr-first", "mcp", sink),
            new ToolCallRecord("mutating-tool", null),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Status = "proposal-failed",
                Summary = "acknowledged write whose follow-up failed",
                MutationAcknowledged = true,
                MutationIdempotencyKey = "idem-1"
            }),
            CancellationToken.None);

        AuditRecord record = Assert.Single(sink.Records);
        Assert.Equal("proposal-failed", record.Status);
        Assert.True(record.Mutated);
    }

    [Theory]
    [InlineData("infrastructure-provisioned", "terraform:operation:apply", "terraform-exact:provision_infrastructure")]
    [InlineData("install-handoff-written", "install-handoff:operation", "install-handoff")]
    [InlineData("install-handoff-verified", "verify-install-handoff:operation", "verify-install-handoff")]
    public async Task InstallEntryAuditFailureRetainsTheCanonicalStepForReconciliation(string status, string suffix, string route)
    {
        using TerraformTestRoot root = new();
        AuditRecoveryStore recovery = new(Path.Combine(root.Path, "audit-recovery"));
        ProvisioningLineage lineage = new("operation", ApplyAuditEventId: "apply-audit");
        OperationResponse response = new(status, "canonical handler result", [], [], [], [],
            ProvisioningLineage: lineage,
            BackendSteps: [new("canonical-write", "fixture", true, "acknowledged", "", true)]);
        await Assert.ThrowsAsync<IOException>(() => ToolCallAuditor.EmitAsync(
            new("session", "execute", "execute-lower-env", "direct-allowed", "mcp", new FailingAuditSink(new IOException("disk full")), recovery),
            new("install_honua", null), response, CancellationToken.None));
        Assert.True(recovery.TryRead("honua-devops:" + suffix, out var evidence));
        Assert.Equal(route, evidence!.Route);
        Assert.Equal("install_honua", evidence.ToolName);
        Assert.Equal("operation", evidence.ProvisioningOperationId);
        if (status == "infrastructure-provisioned") Assert.Equal("apply-audit", evidence.AuditEventId);
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
