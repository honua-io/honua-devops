namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Terminal result of the accept-side filing attempt, reported back to the
/// webhook handler so it can decide whether to consume the event id.
///
/// <para><b>Terminal (success)</b> — the event is fully handled; the handler
/// consumes the id and acks 2xx:</para>
/// <list type="bullet">
///   <item><see cref="Filed"/> — a new issue was created.</item>
///   <item><see cref="DuplicateSkipped"/> — an open issue already tracks the bug;
///   no duplicate filed.</item>
///   <item><see cref="ReportOnly"/> — filing is disabled; the sanitized issue was
///   prepared and logged, not filed.</item>
/// </list>
///
/// <para><b>Transient (failure)</b> — the outcome is unconfirmed; the handler does
/// NOT consume the id and returns a non-2xx so the signed sender retries. The
/// repo-side duplicate search makes a retry a no-op if the write actually landed:</para>
/// <list type="bullet">
///   <item><see cref="SearchFailed"/> — the duplicate check could not confirm the
///   absence of an existing issue.</item>
///   <item><see cref="FilingFailed"/> — the create call failed or errored.</item>
/// </list>
/// </summary>
internal enum BugReportFilingOutcome
{
    Filed,
    DuplicateSkipped,
    ReportOnly,
    SearchFailed,
    FilingFailed,
}
