using System.Text;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// A composed, ready-to-file GitHub issue. The <see cref="DedupeMarker"/> is a
/// stable, machine-readable token embedded in the body so duplicate-issue
/// detection can find an already-filed issue for the same bug.
/// </summary>
internal sealed record GeneratedIssue(
    string Title,
    string Body,
    IReadOnlyList<string> Labels,
    string DedupeMarker);

/// <summary>
/// Builds a <em>sanitized</em> GitHub issue from a <see cref="BugReport"/>.
///
/// Two guarantees hold here:
/// <list type="number">
///   <item>Only reference-shaped fields are ever rendered — ticket id, component,
///   severity/env/service labels, a short summary, and lists of opaque envelope /
///   fixture <em>reference</em> ids. There is no field on <see cref="BugReport"/>
///   for raw customer payloads or bytes, so none can be rendered.</item>
///   <item>Every free-text field is run through <see cref="Redaction.Scrub"/>
///   before it lands in the body, exactly as the GitOps PR body and Jira
///   provenance comment already do, so a secret mis-emitted into a title/summary
///   cannot leak into a product repo.</item>
/// </list>
/// </summary>
internal static class BugReportIssueComposer
{
    private const string MarkerPrefix = "honua-bug-fingerprint:";

    internal static string BuildDedupeMarker(BugReport report)
        => $"<!-- {MarkerPrefix} {Redaction.Scrub(report.DedupeKey)} -->";

    internal static GeneratedIssue Compose(BugReport report, RepoRef repo, IReadOnlyList<string> labels)
    {
        string marker = BuildDedupeMarker(report);
        string title = BuildTitle(report);
        string body = BuildBody(report, repo, marker);
        return new GeneratedIssue(title, body, labels, marker);
    }

    private static string BuildTitle(BugReport report)
    {
        string subject = string.IsNullOrWhiteSpace(report.Title)
            ? $"Bug report from support ticket {report.TicketId}"
            : Redaction.Scrub(report.Title!.Trim());

        // Prefix keeps support-routed issues visually distinct and greppable.
        return $"[support-bug] {subject}";
    }

    private static string BuildBody(BugReport report, RepoRef repo, string marker)
    {
        StringBuilder builder = new();
        builder.AppendLine("Automated bug report routed from honua-support (operator-approved, signed `ticket.bug_report.v1`).");
        builder.AppendLine();
        builder.AppendLine("This issue carries **evidence references only** — no customer payloads or raw bytes.");
        builder.AppendLine();

        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.Append("| Ticket | `").Append(Redaction.Scrub(report.TicketId)).AppendLine("` |");
        builder.Append("| Component | `").Append(Redaction.Scrub(report.Component)).AppendLine("` |");
        builder.Append("| Destination | `").Append(repo.FullName).AppendLine("` |");
        AppendOptionalRow(builder, "Severity", report.Severity);
        AppendOptionalRow(builder, "Environment", report.Environment);
        AppendOptionalRow(builder, "Service", report.Service);
        if (!string.IsNullOrWhiteSpace(report.Fingerprint))
        {
            AppendOptionalRow(builder, "Fingerprint", report.Fingerprint);
        }
        builder.Append("| Reported at | `").Append(report.EmittedAt.ToUniversalTime().ToString("u")).AppendLine("` |");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(report.Summary))
        {
            builder.AppendLine("## Summary");
            builder.AppendLine();
            builder.AppendLine(Redaction.Scrub(report.Summary!.Trim()));
            builder.AppendLine();
        }

        builder.AppendLine("## Evidence references");
        builder.AppendLine();
        AppendRefList(builder, "Envelope references", report.EnvelopeRefs);
        AppendRefList(builder, "Fixture references", report.FixtureRefs);

        if (!string.IsNullOrWhiteSpace(report.TicketUrl))
        {
            builder.AppendLine();
            builder.Append("Source ticket: ").AppendLine(Redaction.Scrub(report.TicketUrl!.Trim()));
        }

        builder.AppendLine();
        builder.AppendLine(marker);
        return builder.ToString();
    }

    private static void AppendOptionalRow(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append("| ").Append(label).Append(" | `").Append(Redaction.Scrub(value.Trim())).AppendLine("` |");
    }

    private static void AppendRefList(StringBuilder builder, string label, IReadOnlyList<string> refs)
    {
        builder.Append("**").Append(label).AppendLine("**");
        builder.AppendLine();
        if (refs.Count == 0)
        {
            builder.AppendLine("- _(none provided)_");
            return;
        }

        foreach (string reference in refs)
        {
            builder.Append("- `").Append(Redaction.Scrub(reference.Trim())).AppendLine("`");
        }
    }
}
