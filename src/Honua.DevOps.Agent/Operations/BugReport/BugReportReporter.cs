using System.Text;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// onAccepted bridge for the bug-report listener — the issue-filing analogue of
/// <see cref="EscalationConsoleReporter"/> and
/// <see cref="Honua.DevOps.Agent.Operations.WorkIntake.WorkIntakeReporter"/>.
///
/// Renders the resolved report + destination to the operator console, then:
/// <list type="bullet">
///   <item>If the issue tracker is disabled (no GitHub token), stays in a
///   plan/report posture: the <em>sanitized</em> issue is composed and logged but
///   not filed, matching the escalation receiver's report-only default.</item>
///   <item>If enabled, runs <b>duplicate-issue detection</b> first and files only
///   when no open issue already references the bug's stable key.</item>
/// </list>
/// The composed body is sanitized and reference-only by construction (see
/// <see cref="BugReportIssueComposer"/>).
/// </summary>
internal sealed class BugReportReporter
{
    private const string Divider = "------ BUG REPORT RECEIVED ------";
    private const string EndDivider = "---------------------------------";

    private readonly IIssueTracker _tracker;
    private readonly IReadOnlyList<string> _labels;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    internal BugReportReporter(
        IIssueTracker tracker,
        IReadOnlyList<string> labels,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        _tracker = tracker;
        _labels = labels;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
    }

    internal async Task<BugReportFilingOutcome> ReportAsync(BugReport report, RepoRef repo, CancellationToken cancellationToken)
    {
        GeneratedIssue issue = BugReportIssueComposer.Compose(report, repo, _labels);
        WriteHeader(report, repo, issue);

        if (!_tracker.IsEnabled)
        {
            _stdout.WriteLine(
                $"[bug-report] issue filing disabled (no GitHub token) — sanitized issue prepared for {repo.FullName} but not filed.");
            return BugReportFilingOutcome.ReportOnly;
        }

        try
        {
            // Duplicate-issue detection BEFORE filing: never open a second issue for
            // a bug already tracked in the destination repo. The search term is the
            // dedupe hash embedded in every filed issue, so a redelivery of a bug we
            // already filed reliably matches.
            IssueSearchResult search = await _tracker.FindOpenIssueAsync(repo, issue.DedupeSearchToken, cancellationToken);
            if (search.IsSuccess && search.DuplicateFound)
            {
                _stdout.WriteLine(
                    $"[bug-report] open issue already tracks `{report.DedupeKey}` in {repo.FullName} ({search.ExistingIssueUrl ?? "url-unknown"}) — not filing a duplicate.");
                return BugReportFilingOutcome.DuplicateSkipped;
            }

            if (!search.IsSuccess)
            {
                // Transient: we could not confirm there is no duplicate. Do not file
                // and do not consume the event id — signal the handler to request a
                // retry rather than risk a possible duplicate.
                _stderr.WriteLine(
                    $"warn: duplicate-issue check failed for {repo.FullName} ({search.Detail}) — not filing; will retry.");
                return BugReportFilingOutcome.SearchFailed;
            }

            IssueFilingResult filing = await _tracker.FileIssueAsync(repo, issue, cancellationToken);
            if (filing.IsSuccess)
            {
                _stdout.WriteLine(
                    $"[bug-report] filed sanitized issue in {repo.FullName}: {filing.IssueUrl ?? "url-unknown"}.");
                return BugReportFilingOutcome.Filed;
            }

            _stderr.WriteLine(
                $"warn: issue filing failed for {repo.FullName}: {filing.Detail} — will retry.");
            return BugReportFilingOutcome.FilingFailed;
        }
        catch (OperationCanceledException)
        {
            // Deadline/shutdown cancellation is handled by the caller (the handler
            // treats a deadline as an unconfirmed, retryable filing).
            throw;
        }
        catch (Exception exception)
        {
            _stderr.WriteLine(
                $"warn: issue filing errored for {repo.FullName}: {exception.GetType().Name} {exception.Message} — will retry.");
            return BugReportFilingOutcome.FilingFailed;
        }
    }

    private void WriteHeader(BugReport report, RepoRef repo, GeneratedIssue issue)
    {
        StringBuilder builder = new();
        builder.AppendLine();
        builder.AppendLine(Divider);
        builder.Append("Ticket: ").AppendLine(report.TicketId);
        builder.Append("Component: ").Append(report.Component)
               .Append(" -> Repo: ").AppendLine(repo.FullName);
        builder.Append("Severity: ").Append(report.Severity ?? "(none)")
               .Append(" | Env: ").Append(report.Environment ?? "(none)")
               .Append(" | Service: ").AppendLine(report.Service ?? "(none)");
        builder.Append("Dedupe key: ").AppendLine(report.DedupeKey);
        builder.Append("Evidence refs: envelopes=").Append(report.EnvelopeRefs.Count)
               .Append(" fixtures=").AppendLine(report.FixtureRefs.Count.ToString());
        builder.Append("Issue title: ").AppendLine(issue.Title);
        builder.AppendLine(EndDivider);
        _stdout.Write(builder.ToString());
    }
}
