using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Tests;

public class OperationJournalTests
{
    [Fact]
    public void ListRecent_ReportsMissingFileTargetCleanly()
    {
        using StringWriter writer = new();
        int exit = OperationJournal.ListRecent("stdout-evidence", 10, writer);
        Assert.Equal(64, exit);
        Assert.Contains("file-backed audit sink", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ListRecent_PrintsTailFromJournal()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-{Guid.NewGuid():n}.jsonl");
        try
        {
            for (int i = 0; i < 5; i++)
            {
                File.AppendAllText(
                    path,
                    "{\"Timestamp\":\"2026-05-20T00:00:0" + i + "Z\",\"OperationId\":\"op-" + i +
                    "\",\"ToolName\":\"describe_environment\",\"Status\":\"ok\",\"Summary\":\"sample\",\"Mutated\":false,\"ExecutionTier\":\"plan\"}\n");
            }

            using StringWriter writer = new();
            int exit = OperationJournal.ListRecent($"file://{path}", 3, writer);

            Assert.Equal(0, exit);
            string output = writer.ToString();
            Assert.Contains("op-4", output, StringComparison.Ordinal);
            Assert.Contains("op-3", output, StringComparison.Ordinal);
            Assert.Contains("op-2", output, StringComparison.Ordinal);
            Assert.DoesNotContain("op-1", output, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ShowOperation_FindsLineByOperationId()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-{Guid.NewGuid():n}.jsonl");
        try
        {
            File.WriteAllText(path,
                "{\"OperationId\":\"abc-123\",\"ToolName\":\"deploy_service_gitops\",\"Status\":\"deploy-planned\"}\n" +
                "{\"OperationId\":\"xyz-999\",\"ToolName\":\"describe_environment\",\"Status\":\"environment-described\"}\n");

            using StringWriter writer = new();
            int exit = OperationJournal.ShowOperation($"file://{path}", "xyz-999", writer);

            Assert.Equal(0, exit);
            Assert.Contains("xyz-999", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ShowOperation_ReturnsNonZeroWhenOperationMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-{Guid.NewGuid():n}.jsonl");
        try
        {
            File.WriteAllText(path, "{\"OperationId\":\"abc-123\"}\n");

            using StringWriter writer = new();
            int exit = OperationJournal.ShowOperation($"file://{path}", "does-not-exist", writer);

            Assert.Equal(66, exit);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
