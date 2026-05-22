using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Configuration;

internal sealed record CliOptions(
    ProviderKind Provider,
    string? Prompt,
    bool Preflight,
    bool Help,
    bool ListTools,
    bool ListOperations,
    string? ShowOperation,
    int OperationLimit,
    bool Listen)
{
    private const string ProviderFlag = "--provider";
    private const string PromptFlag = "--prompt";
    private const string PreflightFlag = "--preflight";
    private const string HelpFlag = "--help";
    private const string ShortHelpFlag = "-h";
    private const string ListToolsFlag = "--list-tools";
    private const string ListOperationsFlag = "--list-operations";
    private const string ShowOperationFlag = "--show-operation";
    private const string LimitFlag = "--limit";
    private const string ListenFlag = "--listen";
    private const string ProviderEnvironmentVariable = "HONUA_DEVOPS_PROVIDER";

    internal const string HelpText = """
honua-devops — AI operator for Honua

Usage: honua-devops [options]

Options:
  --provider <codex|claude|local-llama>
                              Pick the model provider. Defaults to HONUA_DEVOPS_PROVIDER or codex.
                              local-llama covers NVIDIA NIM and other OpenAI-compatible local endpoints.
  --prompt <text>             Single-shot prompt; agent runs once, prints, and exits.
  --preflight                 Validate config and backends without launching the agent.
  --list-tools                Print the operator tool catalogue and exit.
  --list-operations           Print recent operations from the audit journal (requires file:// audit hook).
  --show-operation <id>       Print a single operation record by operation id.
  --limit <n>                 Limit --list-operations to the n most recent records (default 20).
  --listen                    Run the escalation webhook receiver. Requires HONUA_DEVOPS_WEBHOOK_SECRET.
  -h, --help                  Show this help and exit.

Environment (--listen):
  HONUA_DEVOPS_WEBHOOK_SECRET       Required. Shared HMAC-SHA256 secret matching honua-support.
  HONUA_DEVOPS_WEBHOOK_PORT         Optional. TCP port to bind (default 8090).
  HONUA_DEVOPS_WEBHOOK_PATH         Optional. URL path to accept POSTs on (default /escalations).
  HONUA_DEVOPS_WEBHOOK_AUTO_TRIAGE  Optional. When true (default), auto-triage on receive.

Examples:
  honua-devops --preflight
  honua-devops --provider codex --prompt "describe the environment"
  honua-devops --list-tools
  honua-devops --list-operations --limit 50
  honua-devops --show-operation 7d2b9f...
  honua-devops --listen
""";

    internal static CliOptions Parse(string[] args)
    {
        string? providerValue = null;
        string? prompt = null;
        bool preflight = false;
        bool help = false;
        bool listTools = false;
        bool listOperations = false;
        string? showOperation = null;
        int operationLimit = 20;
        bool listen = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals(ProviderFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"{ProviderFlag} requires a value: codex, claude, or local-llama.");
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

            if (argument.Equals(ListOperationsFlag, StringComparison.OrdinalIgnoreCase))
            {
                listOperations = true;
                continue;
            }

            if (argument.Equals(ShowOperationFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"{ShowOperationFlag} requires an operation id.");
                }

                showOperation = args[++index];
                continue;
            }

            if (argument.Equals(ListenFlag, StringComparison.OrdinalIgnoreCase))
            {
                listen = true;
                continue;
            }

            if (argument.Equals(LimitFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int parsedLimit) || parsedLimit < 1)
                {
                    throw new InvalidOperationException($"{LimitFlag} requires a positive integer.");
                }

                operationLimit = parsedLimit;
                index++;
                continue;
            }

            throw new InvalidOperationException(
                $"Unknown argument `{argument}`. Use {ProviderFlag}, {PromptFlag}, {PreflightFlag}, {ListToolsFlag}, {ListOperationsFlag}, {ShowOperationFlag}, {LimitFlag}, {ListenFlag}, or {HelpFlag}.");
        }

        if (help || listTools || listOperations || showOperation is not null)
        {
            return new CliOptions(
                ProviderKind.Codex,
                prompt,
                preflight,
                help,
                listTools,
                listOperations,
                showOperation,
                operationLimit,
                listen);
        }

        string selectedProvider = providerValue ??
                                  Environment.GetEnvironmentVariable(ProviderEnvironmentVariable) ??
                                  ProviderKind.Codex.ToString();

        if (!ProviderKindExtensions.TryParse(selectedProvider, out ProviderKind provider))
        {
            throw new InvalidOperationException(
                $"Invalid provider `{selectedProvider}`. Supported values: codex, claude, local-llama.");
        }

        return new CliOptions(
            provider,
            prompt,
            preflight,
            help,
            listTools,
            listOperations,
            showOperation,
            operationLimit,
            listen);
    }
}
