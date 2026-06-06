using System.Text.RegularExpressions;

namespace Honua.DevOps.Agent.Operations;

internal static partial class Redaction
{
    private const string Placeholder = "<redacted>";

    [GeneratedRegex(
        @"(?i)""?\b(api[_-]?key|x[_-]?api[_-]?key|scoped[_-]?key|authorization|access[_-]?token|token|secret|password|passwd)\b""?\s*(?<sep>[:=])\s*(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^,;&\s""']+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyValuePattern();

    [GeneratedRegex(
        @"(?i)(?<prefix>[?&](?:api[_-]?key|x[_-]?api[_-]?key|access[_-]?token|token|secret|password|passwd)=)(?<value>[^&\s""']+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryStringPattern();

    [GeneratedRegex(
        @"(?i)\b(?<scheme>Bearer|Basic)\s+(?<value>[A-Za-z0-9._\-+/=]{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderPattern();

    internal static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        // Authorization header first so the token isn't left behind when its
        // `authorization=` key/value gets collapsed below.
        string scrubbed = AuthorizationHeaderPattern().Replace(input, match =>
            $"{match.Groups["scheme"].Value} {Placeholder}");
        scrubbed = SensitiveKeyValuePattern().Replace(scrubbed, match =>
            $"{match.Groups[1].Value}{match.Groups["sep"].Value}{Placeholder}");
        scrubbed = SensitiveQueryStringPattern().Replace(scrubbed, match =>
            $"{match.Groups["prefix"].Value}{Placeholder}");
        return scrubbed;
    }
}
