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
