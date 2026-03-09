namespace Honua.DevOps.Agent.Configuration;

internal static class DotEnvLoader
{
    private static readonly string[] DefaultFileNames = [".env", ".env.local"];

    internal static IReadOnlyList<string> LoadDefaultFiles()
    {
        return LoadFromDirectory(Directory.GetCurrentDirectory());
    }

    internal static IReadOnlyList<string> LoadFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Dotenv directory must not be empty.");
        }

        string resolvedDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(resolvedDirectory))
        {
            return [];
        }

        HashSet<string> preservedEnvironmentVariables = Environment
            .GetEnvironmentVariables()
            .Keys
            .Cast<object>()
            .Select(key => key.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        List<string> loadedFiles = [];
        foreach (string fileName in DefaultFileNames)
        {
            string path = Path.Combine(resolvedDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach ((string key, string value) in ParseFile(path))
            {
                if (preservedEnvironmentVariables.Contains(key))
                {
                    continue;
                }

                Environment.SetEnvironmentVariable(key, value);
            }

            loadedFiles.Add(path);
        }

        return loadedFiles;
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Dotenv file path must not be empty.");
        }

        string resolvedPath = Path.GetFullPath(path);
        List<KeyValuePair<string, string>> entries = [];
        int lineNumber = 0;

        foreach (string rawLine in File.ReadLines(resolvedPath))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid dotenv entry at `{resolvedPath}:{lineNumber}`. Expected `KEY=VALUE`.");
            }

            string key = line[..separatorIndex].Trim();
            if (!IsValidKey(key))
            {
                throw new InvalidOperationException(
                    $"Invalid dotenv key `{key}` at `{resolvedPath}:{lineNumber}`.");
            }

            string rawValue = line[(separatorIndex + 1)..];
            string value = ParseValue(rawValue, resolvedPath, lineNumber);
            entries.Add(new KeyValuePair<string, string>(key, value));
        }

        return entries;
    }

    private static string ParseValue(string rawValue, string path, int lineNumber)
    {
        string trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.StartsWith('"'))
        {
            if (trimmed.Length < 2 || !trimmed.EndsWith('"'))
            {
                throw new InvalidOperationException(
                    $"Unterminated double-quoted dotenv value at `{path}:{lineNumber}`.");
            }

            return DecodeDoubleQuotedValue(trimmed[1..^1]);
        }

        if (trimmed.StartsWith('\''))
        {
            if (trimmed.Length < 2 || !trimmed.EndsWith('\''))
            {
                throw new InvalidOperationException(
                    $"Unterminated single-quoted dotenv value at `{path}:{lineNumber}`.");
            }

            return trimmed[1..^1];
        }

        return StripInlineComment(trimmed).TrimEnd();
    }

    private static string DecodeDoubleQuotedValue(string value)
    {
        System.Text.StringBuilder builder = new(value.Length);
        bool escape = false;

        foreach (char character in value)
        {
            if (!escape)
            {
                if (character == '\\')
                {
                    escape = true;
                    continue;
                }

                builder.Append(character);
                continue;
            }

            builder.Append(character switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => character
            });
            escape = false;
        }

        if (escape)
        {
            builder.Append('\\');
        }

        return builder.ToString();
    }

    private static string StripInlineComment(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '#' && index > 0 && char.IsWhiteSpace(value[index - 1]))
            {
                return value[..index];
            }
        }

        return value;
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        if (!(char.IsLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        return key.All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}
