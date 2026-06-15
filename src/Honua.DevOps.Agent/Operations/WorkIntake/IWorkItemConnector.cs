namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Provider seam for the outbound side of a work-intake connector: reading an
/// issue and writing a provenance stub back to it. Concrete implementations
/// (Jira Cloud today; Jira Data Center / ServiceNow later) own their auth and
/// REST shapes. The intake reporter depends only on this interface.
///
/// Scope for this PR is a write-back STUB: <see cref="PostProvenanceStubAsync"/>
/// posts a single "received by honua-devops" comment so the requester can see
/// the queue picked the ticket up. No deliverable drafting, status transition,
/// or preview link yet.
/// </summary>
internal interface IWorkItemConnector
{
    /// <summary>True when the connector is configured and may make calls.</summary>
    bool IsEnabled { get; }

    /// <summary>Reads the raw issue by its provider-native key (e.g. Jira <c>PROJ-123</c>).</summary>
    Task<BackendCallResult> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a provenance "received by honua-devops" comment back to the issue.
    /// Plan-only by construction: this is the only state mutation the intake
    /// connector performs in this PR.
    /// </summary>
    Task<BackendCallResult> PostProvenanceStubAsync(
        WorkItem workItem,
        string message,
        CancellationToken cancellationToken = default);
}
