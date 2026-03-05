using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Configuration;

internal sealed record CliOptions(ProviderKind Provider, string? Prompt)
{
    private const string ProviderFlag = "--provider";
    private const string PromptFlag = "--prompt";
    private const string ProviderEnvironmentVariable = "HONUA_DEVOPS_PROVIDER";

    internal static CliOptions Parse(string[] args)
    {
        string? providerValue = null;
        string? prompt = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals(ProviderFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"{ProviderFlag} requires a value: codex or claude.");
                }

                providerValue = args[++index];
                continue;
            }

            if (argument.Equals(PromptFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"{PromptFlag} requires a text value.");
                }

                prompt = args[++index];
                continue;
            }

            throw new InvalidOperationException($"Unknown argument `{argument}`. Use {ProviderFlag} and {PromptFlag}.");
        }

        string selectedProvider = providerValue ??
                                  Environment.GetEnvironmentVariable(ProviderEnvironmentVariable) ??
                                  ProviderKind.Codex.ToString();

        if (!ProviderKindExtensions.TryParse(selectedProvider, out ProviderKind provider))
        {
            throw new InvalidOperationException(
                $"Invalid provider `{selectedProvider}`. Supported values: codex, claude.");
        }

        return new CliOptions(provider, prompt);
    }
}
