using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Wire subset of a Jira webhook body (Jira Cloud and Data Center share this
/// shape for issue events). JSON is camelCase on the wire. We deliberately read
/// only the fields the intake connector needs to normalize a <see cref="WorkItem"/>
/// and tolerate everything else so Jira can evolve its payload independently.
///
/// Reference event names: <c>jira:issue_created</c>, <c>jira:issue_updated</c>.
/// </summary>
internal sealed record WorkIntakeWebhookPayload(
    [property: JsonPropertyName("webhookEvent")] string? WebhookEvent,
    [property: JsonPropertyName("issue")] WorkIntakeIssue? Issue,
    [property: JsonPropertyName("user")] WorkIntakeUser? User)
{
    internal const string IssueCreatedEvent = "jira:issue_created";
    internal const string IssueUpdatedEvent = "jira:issue_updated";

    // Jira Cloud webhooks are verified via a configured shared secret carried as
    // an HMAC-SHA256 signature header, mirroring the escalation listener contract.
    internal const string SignatureHeader = "X-Hub-Signature";

    // Match JsonSerializerDefaults.Web: camelCase + case-insensitive read.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>True when the event is one we normalize into a work item.</summary>
    internal bool IsSupportedEvent()
        => string.Equals(WebhookEvent, IssueCreatedEvent, StringComparison.Ordinal)
            || string.Equals(WebhookEvent, IssueUpdatedEvent, StringComparison.Ordinal);
}

internal sealed record WorkIntakeIssue(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("self")] string? Self,
    [property: JsonPropertyName("fields")] WorkIntakeFields? Fields);

internal sealed record WorkIntakeFields(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("status")] WorkIntakeNamed? Status,
    [property: JsonPropertyName("issuetype")] WorkIntakeNamed? IssueType,
    [property: JsonPropertyName("project")] WorkIntakeProject? Project,
    [property: JsonPropertyName("reporter")] WorkIntakeUser? Reporter);

internal sealed record WorkIntakeNamed(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record WorkIntakeProject(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record WorkIntakeUser(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("emailAddress")] string? EmailAddress);
