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

    [GeneratedRegex(
        @"^(?i)(api[_-]?key|x[_-]?api[_-]?key|scoped[_-]?key|authorization|access[_-]?token|token|secret|password|passwd)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyNamePattern();

    /// <summary>
    /// True when an argument/field <em>name</em> (e.g. "apiKey", "token") denotes a
    /// secret whose value must never be logged verbatim. Used for structured
    /// key/value pairs where the value carries no inline `key=` prefix for
    /// <see cref="Scrub"/> to latch onto.
    /// </summary>
    internal static bool IsSensitiveKey(string? key)
        => !string.IsNullOrEmpty(key) && SensitiveKeyNamePattern().IsMatch(key.Trim());

    /// <summary>
    /// Redact a structured value by its key: a sensitive key name collapses the
    /// whole value to the placeholder; any other value still runs through
    /// <see cref="Scrub"/> in case it embeds an inline secret.
    /// </summary>
    internal static string ScrubValue(string? key, string? value)
        => IsSensitiveKey(key) ? Placeholder : Scrub(value);

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
