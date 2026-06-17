namespace Honua.DevOps.Agent.Providers;

internal enum ProviderKind
{
    Codex,
    Claude,
    LocalLlama,
    Bedrock
}

internal static class ProviderKindExtensions
{
    internal static bool TryParse(string? value, out ProviderKind provider)
    {
        if (value is null)
        {
            provider = default;
            return false;
        }

        string normalized = value.Trim();

        if (normalized.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Codex;
            return true;
        }

        if (normalized.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Claude;
            return true;
        }

        if (normalized.Equals("local-llama", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("localllama", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("LocalLlama", StringComparison.Ordinal))
        {
            provider = ProviderKind.LocalLlama;
            return true;
        }

        if (normalized.Equals("bedrock", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("aws-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Bedrock;
            return true;
        }

        provider = default;
        return false;
    }

    internal static string ToPrefix(this ProviderKind provider)
    {
        return provider switch
        {
            ProviderKind.Codex => "HONUA_DEVOPS_CODEX",
            ProviderKind.Claude => "HONUA_DEVOPS_CLAUDE",
            ProviderKind.LocalLlama => "HONUA_DEVOPS_LOCAL_LLAMA",
            ProviderKind.Bedrock => "HONUA_DEVOPS_BEDROCK",
            _ => throw new InvalidOperationException($"Provider `{provider}` is not supported.")
        };
    }

    internal static string ToConfigValue(this ProviderKind provider)
    {
        return provider switch
        {
            ProviderKind.Codex => "codex",
            ProviderKind.Claude => "claude",
            ProviderKind.LocalLlama => "local-llama",
            ProviderKind.Bedrock => "bedrock",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.")
        };
    }
}
