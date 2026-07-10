using System.Text;
using System.Text.RegularExpressions;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Renders customer-originated free text inert before it is written into a
/// product-repo GitHub issue. Secret scrubbing (<see cref="Redaction"/>) removes
/// leaked credentials; this sanitizer additionally neutralizes GitHub Markdown
/// side effects so a support summary can never ping a team, backlink another
/// issue, break out of a table, or inject HTML into the destination repo.
///
/// Neutralized in free text: team/user <c>@mentions</c>, issue/PR
/// back-references (<c>#123</c> and the <c>#123</c> tail of a cross-repo
/// <c>owner/repo#123</c>), Markdown link/image brackets, table pipes, and raw
/// HTML. Values destined for an inline code span are stripped of backticks and
/// line breaks so they cannot close the span and escape into live Markdown.
/// </summary>
internal static partial class BugReportSanitizer
{
    /// <summary>Single-line issue titles are capped to this many characters.</summary>
    internal const int MaxTitleLength = 160;

    // A zero-width space breaks GitHub's autolink triggers (@mention, #ref) while
    // staying invisible in the rendered issue.
    private const string ZeroWidthSpace = "\u200B";

    // An '@' that begins a user/org(/team) handle.
    [GeneratedRegex(@"@(?=[A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex MentionTrigger();

    // A '#' immediately followed by a digit — covers a bare `#123` and the `#123`
    // tail of a cross-repo `owner/repo#123` reference.
    [GeneratedRegex(@"#(?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IssueRefTrigger();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Neutralizes a free-text block (e.g. the summary) for safe rendering as
    /// Markdown body text. Secrets are scrubbed first, then Markdown/HTML/mention
    /// triggers are made inert. Line breaks are preserved.
    /// </summary>
    internal static string NeutralizeText(string? value)
    {
        // Scrub secrets on the raw text FIRST: the credential patterns latch onto
        // characters (e.g. `<`, `|`) that markup neutralization would escape into
        // entities, so escaping first could hide a secret from the scrubber.
        string scrubbed = Redaction.Scrub(value);
        return scrubbed.Length == 0 ? string.Empty : NeutralizeMarkup(scrubbed);
    }

    /// <summary>
    /// Neutralizes a value for an issue <em>title</em>: free-text neutralization,
    /// collapsed to a single line, and length-capped.
    /// </summary>
    internal static string NeutralizeTitle(string? value)
    {
        string text = WhitespaceRun().Replace(NeutralizeText(value), " ").Trim();
        if (text.Length > MaxTitleLength)
        {
            text = text[..MaxTitleLength].TrimEnd() + "…";
        }

        return text;
    }

    /// <summary>
    /// Neutralizes a value that the composer wraps in an inline code span. Inside a
    /// code span Markdown/HTML/mention triggers are already inert, so the only
    /// escape is a backtick or newline closing the span — both are removed here.
    /// </summary>
    internal static string NeutralizeCode(string? value)
    {
        string scrubbed = Redaction.Scrub(value);
        if (scrubbed.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(scrubbed.Length);
        foreach (char c in scrubbed)
        {
            if (c == '`')
            {
                continue; // a backtick would close the code span and escape it
            }

            // Collapse CR/LF and other control characters to a space so the value
            // stays on one line inside the span.
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private static string NeutralizeMarkup(string input)
    {
        // Mention/issue-ref triggers first: they operate on the raw '@'/'#', and
        // the HTML/pipe entity-escapes below introduce characters (e.g. the '#' in
        // a numeric entity) that must not be re-processed as triggers.
        string text = MentionTrigger().Replace(input, "@" + ZeroWidthSpace);
        text = IssueRefTrigger().Replace(text, "#" + ZeroWidthSpace);

        StringBuilder builder = new(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '|':
                    builder.Append("&#124;"); // never participate in a Markdown table
                    break;
                case '[':
                    builder.Append("\\[");
                    break;
                case ']':
                    builder.Append("\\]");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
