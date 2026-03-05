namespace Honua.DevOps.Agent.Providers;

internal enum ProviderKind
{
    Codex,
    Claude
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

        if (value.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Codex;
            return true;
        }

        if (value.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Claude;
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
            _ => throw new InvalidOperationException($"Provider `{provider}` is not supported.")
        };
    }
}
