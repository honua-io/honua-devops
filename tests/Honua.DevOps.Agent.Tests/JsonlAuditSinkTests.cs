using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Tests;

public class JsonlAuditSinkTests
{
    [Fact]
    public async Task FileSink_AppendsOneRecordPerLineAndPreservesFields()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-audit-{Guid.NewGuid():n}.jsonl");
        try
        {
            await using (IAuditSink sink = JsonlAuditSink.ForFile(path))
            {
                await sink.WriteAsync(NewRecord("describe_environment", "environment-described", mutated: false));
                await sink.WriteAsync(NewRecord("deploy_service_gitops", "deploy-planned", mutated: true));
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using JsonDocument first = JsonDocument.Parse(lines[0]);
            Assert.Equal("describe_environment", first.RootElement.GetProperty("ToolName").GetString());
            Assert.False(first.RootElement.GetProperty("Mutated").GetBoolean());
            using JsonDocument second = JsonDocument.Parse(lines[1]);
            Assert.Equal("deploy_service_gitops", second.RootElement.GetProperty("ToolName").GetString());
            Assert.True(second.RootElement.GetProperty("Mutated").GetBoolean());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Factory_DispatchesByTarget()
    {
        Assert.IsType<NullAuditSink>(AuditSinkFactory.Create("none"));
        Assert.IsType<NullAuditSink>(AuditSinkFactory.Create("disabled"));

        IAuditSink stdoutSink = AuditSinkFactory.Create("stdout-evidence");
        Assert.IsType<JsonlAuditSink>(stdoutSink);
        Assert.Equal("stdout-evidence", stdoutSink.Target);

        string tempPath = Path.Combine(Path.GetTempPath(), $"honua-audit-target-{Guid.NewGuid():n}.jsonl");
        try
        {
            IAuditSink fileSink = AuditSinkFactory.Create($"file://{tempPath}");
            Assert.IsType<JsonlAuditSink>(fileSink);
            Assert.StartsWith("file:", fileSink.Target);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void Factory_RejectsFileTargetWithEmptyPath()
    {
        Assert.Throws<InvalidOperationException>(() => AuditSinkFactory.Create("file://"));
    }

    private static AuditRecord NewRecord(string tool, string status, bool mutated)
    {
        return new AuditRecord(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: "test-session",
            OperationId: Guid.NewGuid().ToString("n"),
            ToolName: tool,
            Arguments: new Dictionary<string, string> { ["service"] = "roads-api" },
            Status: status,
            Summary: "test summary",
            Mutated: mutated,
            ExecutionMode: "plan",
            ExecutionTier: "plan",
            ApprovalMode: "pr-first",
            Provider: "codex",
            BackendSteps: null,
            Evidence: null);
    }
}
