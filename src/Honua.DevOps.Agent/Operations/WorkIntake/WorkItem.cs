namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Provider-neutral normalization of an inbound work-intake signal (a Jira issue
/// today; ServiceNow later). The intake handler maps each provider's webhook
/// subset onto this shape so the rest of honua-devops never depends on a
/// provider's wire format.
///
/// This PR is inbound + write-back stub only: a WorkItem is logged and a
/// provenance "received" comment is written back to the source ticket. No
/// deliverable is drafted, promoted, or bound to an environment yet.
/// </summary>
internal sealed record WorkItem(
    string Provider,
    string ExternalId,
    string ExternalUrl,
    string Title,
    string Kind,
    string Status,
    string Project,
    string? Environment,
    string? Requester)
{
    internal const string JiraProvider = "jira";

    /// <summary>One-line operator-console summary; never fabricates absent fields.</summary>
    internal string Describe()
    {
        string requester = string.IsNullOrWhiteSpace(Requester) ? "(unknown)" : Requester!;
        string environment = string.IsNullOrWhiteSpace(Environment) ? "(none)" : Environment!;
        return $"{Provider}:{ExternalId} [{Kind}/{Status}] project={Project} env={environment} requester={requester} — {Title}";
    }
}
