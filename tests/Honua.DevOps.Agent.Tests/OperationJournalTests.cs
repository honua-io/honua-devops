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
                    "{\"Timestamp\":\"2026-05-20T00:00:0" + i + "Z\",\"AuditEventId\":\"audit-" + i +
                    "\",\"ToolName\":\"describe_environment\",\"Status\":\"ok\",\"Summary\":\"sample\",\"Mutated\":false,\"ExecutionTier\":\"plan\"}\n");
            }

            using StringWriter writer = new();
            int exit = OperationJournal.ListRecent($"file://{path}", 3, writer);

            Assert.Equal(0, exit);
            string output = writer.ToString();
            Assert.Contains("audit-4", output, StringComparison.Ordinal);
            Assert.Contains("audit-3", output, StringComparison.Ordinal);
            Assert.Contains("audit-2", output, StringComparison.Ordinal);
            Assert.DoesNotContain("audit-1", output, StringComparison.Ordinal);
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
                "{\"AuditEventId\":\"abc-123\",\"ToolName\":\"deploy_service_gitops\",\"Status\":\"deploy-planned\"}\n" +
                "{\"AuditEventId\":\"xyz-999\",\"ToolName\":\"describe_environment\",\"Status\":\"environment-described\"}\n");

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
    public void Journal_ReadsLegacyOperationIdOnlyAsAuditCompatibilityAlias()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-legacy-{Guid.NewGuid():n}.jsonl");
        try
        {
            File.WriteAllText(path,
                "{\"OperationId\":\"legacy-audit-1\",\"ToolName\":\"describe_environment\",\"Status\":\"ok\"}\n");

            OperationSearchResult result = OperationJournal.Find(
                $"file://{path}", null, null, null, 10);

            RecentOperation record = Assert.Single(result.Operations);
            Assert.Equal("legacy-audit-1", record.AuditEventId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ShowOperation_MatchesParsedIdNotRawSubstring()
    {
        // The earlier record mentions the later record's id inside its Summary field.
        // A raw substring scan would return the wrong (earlier) record; field-matching
        // on the parsed AuditEventId must return the record whose id actually equals it.
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-{Guid.NewGuid():n}.jsonl");
        try
        {
            File.WriteAllText(path,
                "{\"AuditEventId\":\"audit-aaaa\",\"ToolName\":\"deploy_service_gitops\",\"Status\":\"deploy-planned\",\"Summary\":\"superseded audit-bbbb\"}\n" +
                "{\"AuditEventId\":\"audit-bbbb\",\"ToolName\":\"describe_environment\",\"Status\":\"environment-described\",\"Summary\":\"target record\"}\n");

            using StringWriter writer = new();
            int exit = OperationJournal.ShowOperation($"file://{path}", "audit-bbbb", writer);

            Assert.Equal(0, exit);
            string output = writer.ToString();
            Assert.Contains("environment-described", output, StringComparison.Ordinal);
            Assert.DoesNotContain("deploy-planned", output, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Find_AppliesToolMutatedAndStatusFilters()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-{Guid.NewGuid():n}.jsonl");
        try
        {
            File.WriteAllText(path,
                "{\"AuditEventId\":\"audit-1\",\"Timestamp\":\"2026-05-20T00:00:01Z\",\"ToolName\":\"describe_environment\",\"Status\":\"environment-described\",\"Summary\":\"\",\"Mutated\":false,\"ExecutionTier\":\"plan\"}\n" +
                "{\"AuditEventId\":\"audit-2\",\"Timestamp\":\"2026-05-20T00:00:02Z\",\"ToolName\":\"deploy_service_gitops\",\"Status\":\"deploy-planned\",\"Summary\":\"\",\"Mutated\":true,\"ExecutionTier\":\"execute-lower-env\"}\n" +
                "{\"AuditEventId\":\"audit-3\",\"Timestamp\":\"2026-05-20T00:00:03Z\",\"ToolName\":\"deploy_service_gitops\",\"Status\":\"deploy-rollback-submitted\",\"Summary\":\"\",\"Mutated\":true,\"ExecutionTier\":\"execute-lower-env\"}\n" +
                "{\"AuditEventId\":\"audit-4\",\"Timestamp\":\"2026-05-20T00:00:04Z\",\"ToolName\":\"analyze_logs\",\"Status\":\"analysis-ready\",\"Summary\":\"\",\"Mutated\":false,\"ExecutionTier\":\"plan\"}\n");

            OperationSearchResult deploys = OperationJournal.Find(
                $"file://{path}",
                toolFilter: "deploy_service_gitops",
                mutatedOnly: true,
                statusContains: null,
                limit: 10);
            Assert.Equal("operations-found", deploys.Status);
            Assert.Equal(2, deploys.Matched);
            Assert.Equal(new[] { "audit-3", "audit-2" }, deploys.Operations.Select(o => o.AuditEventId).ToArray());

            OperationSearchResult rollback = OperationJournal.Find(
                $"file://{path}",
                toolFilter: null,
                mutatedOnly: null,
                statusContains: "rollback",
                limit: 10);
            Assert.Single(rollback.Operations);
            Assert.Equal("audit-3", rollback.Operations[0].AuditEventId);

            OperationSearchResult readsOnly = OperationJournal.Find(
                $"file://{path}",
                toolFilter: null,
                mutatedOnly: false,
                statusContains: null,
                limit: 10);
            Assert.Equal(4, readsOnly.Matched);

            OperationSearchResult limited = OperationJournal.Find(
                $"file://{path}",
                toolFilter: null,
                mutatedOnly: null,
                statusContains: null,
                limit: 2);
            Assert.Equal(2, limited.Operations.Count);
            Assert.Equal("audit-4", limited.Operations[0].AuditEventId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Find_ReportsUnavailableWhenTargetIsNotFile()
    {
        OperationSearchResult result = OperationJournal.Find(
            "stdout-evidence",
            toolFilter: null,
            mutatedOnly: null,
            statusContains: null,
            limit: 10);
        Assert.Equal("audit-journal-unavailable", result.Status);
        Assert.Empty(result.Operations);
    }

    [Fact]
    public void ShowOperation_ReturnsNonZeroWhenOperationMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-journal-{Guid.NewGuid():n}.jsonl");
        try
        {
            File.WriteAllText(path, "{\"AuditEventId\":\"abc-123\"}\n");

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
