using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Configuration;

internal sealed record CliOptions(ProviderKind Provider, string? Prompt, bool Preflight, bool Help, bool ListTools)
{
    private const string ProviderFlag = "--provider";
    private const string PromptFlag = "--prompt";
    private const string PreflightFlag = "--preflight";
    private const string HelpFlag = "--help";
    private const string ShortHelpFlag = "-h";
    private const string ListToolsFlag = "--list-tools";
    private const string ProviderEnvironmentVariable = "HONUA_DEVOPS_PROVIDER";

    internal const string HelpText = """
honua-devops — AI operator for Honua

Usage: honua-devops [options]

Options:
  --provider <codex|claude>   Pick the model provider. Defaults to HONUA_DEVOPS_PROVIDER or codex.
  --prompt <text>             Single-shot prompt; agent runs once, prints, and exits.
  --preflight                 Validate config and backends without launching the agent.
  --list-tools                Print the operator tool catalogue and exit.
  -h, --help                  Show this help and exit.

Examples:
  honua-devops --preflight
  honua-devops --provider codex --prompt "describe the environment"
  honua-devops --list-tools
""";

    internal static CliOptions Parse(string[] args)
    {
        string? providerValue = null;
        string? prompt = null;
        bool preflight = false;
        bool help = false;
        bool listTools = false;

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

            if (argument.Equals(PreflightFlag, StringComparison.OrdinalIgnoreCase))
            {
                preflight = true;
                continue;
            }

            if (argument.Equals(HelpFlag, StringComparison.OrdinalIgnoreCase)
                || argument.Equals(ShortHelpFlag, StringComparison.OrdinalIgnoreCase))
            {
                help = true;
                continue;
            }

            if (argument.Equals(ListToolsFlag, StringComparison.OrdinalIgnoreCase))
            {
                listTools = true;
                continue;
            }

            throw new InvalidOperationException(
                $"Unknown argument `{argument}`. Use {ProviderFlag}, {PromptFlag}, {PreflightFlag}, {ListToolsFlag}, or {HelpFlag}.");
        }

        string selectedProvider = providerValue ??
                                  Environment.GetEnvironmentVariable(ProviderEnvironmentVariable) ??
                                  ProviderKind.Codex.ToString();

        if (!ProviderKindExtensions.TryParse(selectedProvider, out ProviderKind provider))
        {
            throw new InvalidOperationException(
                $"Invalid provider `{selectedProvider}`. Supported values: codex, claude.");
        }

        return new CliOptions(provider, prompt, preflight, help, listTools);
    }
}
