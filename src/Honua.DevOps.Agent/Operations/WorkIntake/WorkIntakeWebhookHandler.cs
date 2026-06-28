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
    // Per-request deadline for the onAccepted callback. The callback does a synchronous
    // outbound write-back to the source system (the provenance comment), whose only other
    // bound is the connector's HttpClient timeout (~20s). Without a tighter deadline a slow
    // backend holds the inbound delivery open long enough that the sender (e.g. Jira) hits its
    // OWN webhook-delivery timeout and RETRIES — and because the provenance comment is a
    // non-idempotent POST, each retry posts a duplicate "Received by honua-devops" comment.
    // Bounding the callback to a few seconds returns the 202 well inside a typical sender
    // delivery window, so the inbound ack — not the outbound write — drives the response. The
    // provenance write is best-effort by design (WorkIntakeReporter already swallows its own
    // failures), so a timed-out write-back still accepts the item.
    internal static readonly TimeSpan DefaultAcceptedDeadline = TimeSpan.FromSeconds(5);

    private readonly IIntakeSignatureVerifier _verifier;
    private readonly string _provider;
    private readonly string? _projectFilter;
    private readonly Func<WorkItem, CancellationToken, Task>? _onAccepted;
    private readonly TimeSpan _acceptedDeadline;

    internal WorkIntakeWebhookHandler(
        IIntakeSignatureVerifier verifier,
        string provider = WorkItem.JiraProvider,
        string? projectFilter = null,
        Func<WorkItem, CancellationToken, Task>? onAccepted = null,
        TimeSpan? acceptedDeadline = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _provider = string.IsNullOrWhiteSpace(provider) ? WorkItem.JiraProvider : provider.Trim();
        _projectFilter = string.IsNullOrWhiteSpace(projectFilter) ? null : projectFilter.Trim();
        _onAccepted = onAccepted;
        _acceptedDeadline = acceptedDeadline is { } deadline && deadline > TimeSpan.Zero
            ? deadline
            : DefaultAcceptedDeadline;
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
            // Bound the write-back so a slow backend cannot hold the inbound delivery open past
            // the sender's webhook-delivery timeout (which would trigger a duplicate-comment
            // retry). The deadline is linked to the listener token so shutdown still cancels.
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_acceptedDeadline);
            try
            {
                await _onAccepted(workItem, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // The write-back exceeded its per-request deadline. The item is still accepted
                // (the provenance comment is best-effort); ack now rather than risk a sender
                // delivery-retry that would post a duplicate comment.
                return new WorkIntakeHandlerResult(202, "accepted-writeback-deadline", workItem);
            }
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
