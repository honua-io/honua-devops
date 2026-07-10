namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>Outcome of a duplicate-issue search against the destination repo.</summary>
internal sealed record IssueSearchResult(
    bool IsSuccess,
    bool DuplicateFound,
    string? ExistingIssueUrl,
    string Detail);

/// <summary>Outcome of filing an issue.</summary>
internal sealed record IssueFilingResult(
    bool IsSuccess,
    string? IssueUrl,
    string Detail);

/// <summary>
/// Provider seam for the outbound issue tracker (GitHub today). The bug-report
/// adapter depends only on this interface so the pipeline can be unit-tested with
/// a fake and so no live token is needed in tests. Concrete implementations own
/// their auth, host-allowlist, and REST shapes, mirroring
/// <see cref="Honua.DevOps.Agent.Operations.WorkIntake.IWorkItemConnector"/>.
/// </summary>
internal interface IIssueTracker
{
    /// <summary>True when the tracker is configured and may make calls.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Looks for an already-open issue in <paramref name="repo"/> whose body carries
    /// <paramref name="dedupeSearchToken"/> — the SHA-256 dedupe hash that the
    /// composer embeds in the issue's dedupe marker. Searching the same hash that
    /// was filed guarantees a just-filed issue is findable on a redelivery. Used to
    /// avoid filing a second issue for a bug that is already tracked.
    /// </summary>
    Task<IssueSearchResult> FindOpenIssueAsync(
        RepoRef repo,
        string dedupeSearchToken,
        CancellationToken cancellationToken = default);

    /// <summary>Files the composed issue into <paramref name="repo"/>.</summary>
    Task<IssueFilingResult> FileIssueAsync(
        RepoRef repo,
        GeneratedIssue issue,
        CancellationToken cancellationToken = default);
}
