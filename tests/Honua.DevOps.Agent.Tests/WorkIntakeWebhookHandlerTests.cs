using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.WorkIntake;

namespace Honua.DevOps.Agent.Tests;

public class WorkIntakeWebhookHandlerTests
{
    private const string Secret = "intake-test-secret";

    [Fact]
    public async Task Handler_Accepts_And_NormalizesValidJiraEvent()
    {
        WorkItem? captured = null;
        WorkIntakeWebhookHandler handler = new(
            new JiraCloudSignatureVerifier(Secret),
            provider: WorkItem.JiraProvider,
            projectFilter: null,
            onAccepted: (workItem, _) =>
            {
                captured = workItem;
                return Task.CompletedTask;
            });

        string json = BuildJiraIssuePayload();
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("accepted", result.Reason);
        Assert.NotNull(captured);
        Assert.Equal("jira", captured!.Provider);
        Assert.Equal("GIS-42", captured.ExternalId);
        Assert.Equal("New parcel layer for downtown", captured.Title);
        Assert.Equal("Task", captured.Kind);
        Assert.Equal("To Do", captured.Status);
        Assert.Equal("GIS", captured.Project);
        Assert.Equal("Dana Steward", captured.Requester);
        Assert.Equal("https://acme.atlassian.net/rest/api/3/issue/10042", captured.ExternalUrl);
        Assert.Null(captured.Environment);
    }

    [Fact]
    public async Task Handler_Rejects_InvalidSignature()
    {
        bool invoked = false;
        WorkIntakeWebhookHandler handler = new(
            new JiraCloudSignatureVerifier(Secret),
            onAccepted: (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            });

        byte[] body = Encoding.UTF8.GetBytes(BuildJiraIssuePayload());

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, "sha256=" + new string('0', 64), CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("invalid-signature", result.Reason);
        Assert.Null(result.WorkItem);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Handler_Rejects_WrongEvent()
    {
        WorkIntakeWebhookHandler handler = new(new JiraCloudSignatureVerifier(Secret));

        string json = JsonSerializer.Serialize(new
        {
            webhookEvent = "jira:issue_deleted",
            issue = new { key = "GIS-1" }
        });
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.StartsWith("unexpected-event", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.WorkItem);
    }

    [Fact]
    public async Task Handler_Rejects_MalformedJson()
    {
        WorkIntakeWebhookHandler handler = new(new JiraCloudSignatureVerifier(Secret));

        const string json = "{not json";
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("malformed-json", result.Reason);
    }

    [Fact]
    public async Task Handler_Rejects_MissingIssueKey()
    {
        WorkIntakeWebhookHandler handler = new(new JiraCloudSignatureVerifier(Secret));

        string json = JsonSerializer.Serialize(new { webhookEvent = "jira:issue_created" });
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("missing-issue-key", result.Reason);
    }

    [Fact]
    public async Task Handler_SkipsIssuesOutsideProjectFilter()
    {
        bool invoked = false;
        WorkIntakeWebhookHandler handler = new(
            new JiraCloudSignatureVerifier(Secret),
            provider: WorkItem.JiraProvider,
            projectFilter: "OPS",
            onAccepted: (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            });

        string json = BuildJiraIssuePayload();
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.StartsWith("project-filtered", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.WorkItem);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Handler_BoundsSlowWriteBack_AcksWithDeadlineReason()
    {
        // A write-back that outlives the per-request deadline must not hold the inbound
        // delivery open (that is what triggers a sender retry and a duplicate provenance
        // comment). The handler cancels it and still returns 202 with the work item, so the
        // inbound ack — not the slow outbound write — drives the response.
        WorkIntakeWebhookHandler handler = new(
            new JiraCloudSignatureVerifier(Secret),
            provider: WorkItem.JiraProvider,
            projectFilter: null,
            onAccepted: async (_, token) =>
            {
                // Simulate a slow backend that respects cancellation (as the connector's
                // HttpClient does): block on the deadline token until it trips.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            acceptedDeadline: TimeSpan.FromMilliseconds(50));

        string json = BuildJiraIssuePayload();
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        WorkIntakeHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("accepted-writeback-deadline", result.Reason);
        Assert.NotNull(result.WorkItem);
        Assert.Equal("GIS-42", result.WorkItem!.ExternalId);
    }

    [Fact]
    public async Task Handler_PropagatesListenerShutdownCancellation()
    {
        // Shutdown of the listener (the outer token) is a real cancellation, not a write-back
        // deadline; it must propagate rather than be masked as an accepted-writeback-deadline.
        using CancellationTokenSource shutdown = new();
        WorkIntakeWebhookHandler handler = new(
            new JiraCloudSignatureVerifier(Secret),
            provider: WorkItem.JiraProvider,
            projectFilter: null,
            onAccepted: async (_, token) =>
            {
                shutdown.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            acceptedDeadline: TimeSpan.FromSeconds(30));

        string json = BuildJiraIssuePayload();
        byte[] body = Encoding.UTF8.GetBytes(json);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, json);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(body, signature, shutdown.Token));
    }

    private static string BuildJiraIssuePayload()
    {
        object body = new
        {
            webhookEvent = "jira:issue_created",
            issue = new
            {
                key = "GIS-42",
                self = "https://acme.atlassian.net/rest/api/3/issue/10042",
                fields = new
                {
                    summary = "New parcel layer for downtown",
                    status = new { name = "To Do" },
                    issuetype = new { name = "Task" },
                    project = new { key = "GIS", name = "GIS Department" },
                    reporter = new { accountId = "acc-1", displayName = "Dana Steward", emailAddress = "dana@acme.test" }
                }
            },
            user = new { accountId = "acc-9", displayName = "Webhook User" }
        };

        return JsonSerializer.Serialize(body, WorkIntakeWebhookPayload.JsonOptions);
    }
}
