using System.Security.Cryptography;
using System.Text;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// A composed, ready-to-file GitHub issue. The <see cref="DedupeMarker"/> is a
/// stable, machine-readable token embedded in the body so duplicate-issue
/// detection can find an already-filed issue for the same bug.
/// <see cref="DedupeSearchToken"/> is the exact string to search the destination
/// repo for — the same hash embedded in the marker — so a just-filed issue is
/// always findable on a redelivery.
/// </summary>
internal sealed record GeneratedIssue(
    string Title,
    string Body,
    IReadOnlyList<string> Labels,
    string DedupeMarker,
    string DedupeSearchToken);

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

    /// <summary>
    /// The dedupe token embedded in the marker and used as the repo search term: a
    /// SHA-256 hex digest of the dedupe key. Hashing (not the raw key) means no
    /// customer-controlled bytes reach the <c>&lt;!-- ... --&gt;</c> comment, so a
    /// key containing <c>--&gt;</c> cannot break out of it; a hex string never can.
    /// </summary>
    internal static string ComputeDedupeHash(BugReport report)
        => ComputeDedupeHash(report.DedupeKey);

    internal static string ComputeDedupeHash(string dedupeKey)
    {
        // Normalize: the dedupe key falls back to the always-present ticket id, so
        // an empty key is a defensive case only; a stable sentinel keeps the hash
        // representable rather than hashing an empty string.
        string normalized = dedupeKey?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            normalized = "(empty-dedupe-key)";
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string BuildDedupeMarker(BugReport report)
        => $"<!-- {MarkerPrefix} {ComputeDedupeHash(report)} -->";

    internal static GeneratedIssue Compose(BugReport report, RepoRef repo, IReadOnlyList<string> labels)
    {
        string hash = ComputeDedupeHash(report);
        string marker = $"<!-- {MarkerPrefix} {hash} -->";
        string title = BuildTitle(report);
        string body = BuildBody(report, repo, marker);
        return new GeneratedIssue(title, body, labels, marker, hash);
    }

    private static string BuildTitle(BugReport report)
    {
        // Neutralize the whole subject (mentions/refs/HTML), collapse to a single
        // line, and length-cap it so a crafted title cannot ping a team, backlink
        // an issue, or wrap onto multiple lines in the destination repo.
        string rawSubject = string.IsNullOrWhiteSpace(report.Title)
            ? $"Bug report from support ticket {report.TicketId}"
            : report.Title!;

        string subject = BugReportSanitizer.NeutralizeTitle(rawSubject);
        if (subject.Length == 0)
        {
            subject = "Bug report from support ticket";
        }

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
        builder.Append("| Ticket | `").Append(BugReportSanitizer.NeutralizeCode(report.TicketId)).AppendLine("` |");
        builder.Append("| Component | `").Append(BugReportSanitizer.NeutralizeCode(report.Component)).AppendLine("` |");
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
            builder.AppendLine(BugReportSanitizer.NeutralizeText(report.Summary));
            builder.AppendLine();
        }

        builder.AppendLine("## Evidence references");
        builder.AppendLine();
        AppendRefList(builder, "Envelope references", report.EnvelopeRefs);
        AppendRefList(builder, "Fixture references", report.FixtureRefs);

        if (!string.IsNullOrWhiteSpace(report.TicketUrl))
        {
            builder.AppendLine();
            // Wrap the customer-supplied URL in a code span so it is inert text (no
            // autolink, no Markdown link injection) in the destination repo.
            builder.Append("Source ticket: `").Append(BugReportSanitizer.NeutralizeCode(report.TicketUrl)).AppendLine("`");
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

        builder.Append("| ").Append(label).Append(" | `").Append(BugReportSanitizer.NeutralizeCode(value)).AppendLine("` |");
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
            builder.Append("- `").Append(BugReportSanitizer.NeutralizeCode(reference)).AppendLine("`");
        }
    }
}
