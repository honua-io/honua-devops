using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Processes a single inbound work-intake webhook request. The HTTP transport
/// (HttpListener) is kept separate so this can be unit-tested by passing in the
/// raw body and headers — no socket needed, exactly like
/// <see cref="EscalationWebhookHandler"/>.
///
/// Pipeline: verify signature → deserialize → validate event → normalize to a
/// <see cref="WorkItem"/> → invoke <c>onAccepted</c>. Optional project filter
/// drops issues from projects the operator did not opt into.
/// </summary>
internal sealed class WorkIntakeWebhookHandler
{
    private readonly IIntakeSignatureVerifier _verifier;
    private readonly string _provider;
    private readonly string? _projectFilter;
    private readonly Func<WorkItem, CancellationToken, Task>? _onAccepted;

    internal WorkIntakeWebhookHandler(
        IIntakeSignatureVerifier verifier,
        string provider = WorkItem.JiraProvider,
        string? projectFilter = null,
        Func<WorkItem, CancellationToken, Task>? onAccepted = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _provider = string.IsNullOrWhiteSpace(provider) ? WorkItem.JiraProvider : provider.Trim();
        _projectFilter = string.IsNullOrWhiteSpace(projectFilter) ? null : projectFilter.Trim();
        _onAccepted = onAccepted;
    }

    internal async Task<WorkIntakeHandlerResult> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        if (!_verifier.Verify(body, signatureHeader))
        {
            return new WorkIntakeHandlerResult(401, "invalid-signature", WorkItem: null);
        }

        WorkIntakeWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WorkIntakeWebhookPayload>(
                body,
                WorkIntakeWebhookPayload.JsonOptions);
        }
        catch (JsonException)
        {
            return new WorkIntakeHandlerResult(400, "malformed-json", WorkItem: null);
        }

        if (payload is null)
        {
            return new WorkIntakeHandlerResult(400, "empty-payload", WorkItem: null);
        }

        if (!payload.IsSupportedEvent())
        {
            return new WorkIntakeHandlerResult(400, $"unexpected-event:{payload.WebhookEvent ?? "(none)"}", WorkItem: null);
        }

        if (payload.Issue?.Key is null || string.IsNullOrWhiteSpace(payload.Issue.Key))
        {
            return new WorkIntakeHandlerResult(400, "missing-issue-key", WorkItem: null);
        }

        WorkItem workItem = Normalize(payload);

        if (_projectFilter is not null &&
            !string.Equals(workItem.Project, _projectFilter, StringComparison.OrdinalIgnoreCase))
        {
            // Accepted-but-skipped: the webhook authenticated fine, it just is not
            // for a project this operator opted into. 202 keeps Jira from retrying.
            return new WorkIntakeHandlerResult(202, $"project-filtered:{workItem.Project}", WorkItem: null);
        }

        if (_onAccepted is not null)
        {
            await _onAccepted(workItem, cancellationToken);
        }

        return new WorkIntakeHandlerResult(202, "accepted", workItem);
    }

    private WorkItem Normalize(WorkIntakeWebhookPayload payload)
    {
        WorkIntakeIssue issue = payload.Issue!;
        WorkIntakeFields? fields = issue.Fields;
        WorkIntakeUser? requester = fields?.Reporter ?? payload.User;

        string project = fields?.Project?.Key
            ?? fields?.Project?.Name
            ?? "(unknown)";
        string? requesterLabel = requester?.DisplayName
            ?? requester?.EmailAddress
            ?? requester?.AccountId;

        return new WorkItem(
            Provider: _provider,
            ExternalId: issue.Key!.Trim(),
            ExternalUrl: NormalizeUrl(issue.Self),
            Title: string.IsNullOrWhiteSpace(fields?.Summary) ? "(no summary)" : fields!.Summary!.Trim(),
            Kind: string.IsNullOrWhiteSpace(fields?.IssueType?.Name) ? "(unknown)" : fields!.IssueType!.Name!.Trim(),
            Status: string.IsNullOrWhiteSpace(fields?.Status?.Name) ? "(unknown)" : fields!.Status!.Name!.Trim(),
            Project: project,
            // No environment field on a vanilla Jira issue; left null rather than
            // fabricated. A later PR can map a custom field to it.
            Environment: null,
            Requester: requesterLabel);
    }

    private static string NormalizeUrl(string? self)
        => string.IsNullOrWhiteSpace(self) ? string.Empty : self!.Trim();
}

internal sealed record WorkIntakeHandlerResult(
    int StatusCode,
    string Reason,
    WorkItem? WorkItem);
