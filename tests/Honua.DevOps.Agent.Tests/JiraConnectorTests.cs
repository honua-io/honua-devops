using System.Net.Http;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.WorkIntake;

namespace Honua.DevOps.Agent.Tests;

public class JiraConnectorTests
{
    [Fact]
    public async Task GetIssueAsync_CallsRestV3IssueEndpointWithBasicAuth()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { key = "GIS-42" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using JiraConnector connector = new(CreateConfiguration(), httpClient);

        BackendCallResult result = await connector.GetIssueAsync("GIS-42");

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", captured.Method);
        Assert.Contains("/rest/api/3/issue/GIS-42", captured.Uri, StringComparison.Ordinal);
        Assert.Equal("Basic", captured.AuthorizationScheme);

        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("ops@acme.test:jira-token"));
        Assert.Equal(expected, captured.AuthorizationParameter);
    }

    [Fact]
    public async Task PostProvenanceStubAsync_PostsAdfCommentPayload()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { id = "100" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using JiraConnector connector = new(CreateConfiguration(), httpClient);

        WorkItem workItem = SampleWorkItem();
        BackendCallResult result = await connector.PostProvenanceStubAsync(workItem, "Received by honua-devops.");

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/rest/api/3/issue/GIS-42/comment", captured.Uri, StringComparison.Ordinal);
        Assert.NotNull(captured.Body);

        using JsonDocument doc = JsonDocument.Parse(captured.Body!);
        JsonElement bodyEl = doc.RootElement.GetProperty("body");
        Assert.Equal("doc", bodyEl.GetProperty("type").GetString());
        Assert.Equal(1, bodyEl.GetProperty("version").GetInt32());
        string text = bodyEl
            .GetProperty("content")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;
        Assert.Equal("Received by honua-devops.", text);
    }

    [Fact]
    public async Task GetIssueAsync_ReturnsDisabledWhenBaseUriUnset()
    {
        using JiraConnector connector = new(CreateConfiguration(disableBaseUri: true));

        BackendCallResult result = await connector.GetIssueAsync("GIS-42");

        Assert.False(result.IsSuccess);
        Assert.Equal("jira-disabled", result.Detail);
        Assert.False(connector.IsEnabled);
    }

    [Fact]
    public async Task PostProvenanceStubAsync_RejectsHostNotInAllowlist()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { id = "100" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using JiraConnector connector = new(
            CreateConfiguration(allowedHosts: ["other.atlassian.net"]),
            httpClient);

        BackendCallResult result = await connector.PostProvenanceStubAsync(SampleWorkItem(), "hi");

        Assert.False(result.IsSuccess);
        Assert.Equal("jira-host-rejected", result.Detail);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task PostProvenanceStubAsync_RejectsWhenAllowlistEmpty()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { id = "100" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using JiraConnector connector = new(CreateConfiguration(allowedHosts: []), httpClient);

        BackendCallResult result = await connector.PostProvenanceStubAsync(SampleWorkItem(), "hi");

        Assert.False(result.IsSuccess);
        Assert.Equal("jira-host-rejected", result.Detail);
        Assert.Empty(handler.CapturedRequests);
    }

    private static WorkItem SampleWorkItem()
        => new(
            Provider: "jira",
            ExternalId: "GIS-42",
            ExternalUrl: "https://acme.atlassian.net/browse/GIS-42",
            Title: "New parcel layer",
            Kind: "Task",
            Status: "To Do",
            Project: "GIS",
            Environment: null,
            Requester: "Dana Steward");

    private static WorkIntakeConfiguration CreateConfiguration(
        bool disableBaseUri = false,
        IReadOnlyList<string>? allowedHosts = null)
    {
        return new WorkIntakeConfiguration(
            Provider: IntakeProvider.Jira,
            WebhookSecret: "secret",
            Port: WorkIntakeConfiguration.DefaultPort,
            Path: WorkIntakeConfiguration.DefaultPath,
            AllowedHosts: allowedHosts ?? ["acme.atlassian.net"],
            AutoDraft: false,
            JiraBaseUri: disableBaseUri ? null : new Uri("https://acme.atlassian.net"),
            JiraApiToken: "jira-token",
            JiraUserEmail: "ops@acme.test",
            ProjectFilter: null);
    }
}
