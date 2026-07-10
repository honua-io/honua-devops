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
    bool Listen,
    bool IntakeListen,
    bool BugReportListen,
    bool Mcp,
    string? AwaitApproval)
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
    private const string IntakeListenFlag = "--intake-listen";
    private const string BugReportListenFlag = "--bugreport-listen";
    private const string McpFlag = "--mcp";
    private const string AwaitApprovalFlag = "--await-approval";
    private const string ProviderEnvironmentVariable = "HONUA_DEVOPS_PROVIDER";

    internal const string HelpText = """
honua-devops — AI operator for Honua

Usage: honua-devops [options]

Options:
  --provider <codex|claude|local-llama|bedrock>
                              Pick the model provider. Defaults to HONUA_DEVOPS_PROVIDER or codex.
                              local-llama covers NVIDIA NIM and other OpenAI-compatible local endpoints.
                              bedrock runs Claude on Amazon Bedrock (Converse API, AWS IAM credential chain).
  --prompt <text>             Single-shot prompt; agent runs once, prints, and exits.
  --preflight                 Validate config and backends without launching the agent.
  --list-tools                Print the operator tool catalogue and exit.
  --list-operations           Print recent operations from the audit journal (requires file:// audit hook).
  --show-operation <id>       Print a single operation record by operation id.
  --limit <n>                 Limit --list-operations to the n most recent records (default 20).
  --listen                    Run the escalation webhook receiver. Requires HONUA_DEVOPS_WEBHOOK_SECRET.
  --intake-listen             Run the work-intake webhook receiver (Jira). Enterprise edition only.
                              Requires HONUA_DEVOPS_INTAKE_PROVIDER=jira and HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET.
  --bugreport-listen          Run the signed ticket.bug_report.v1 issue adapter: verify + freshness +
                              idempotency, resolve the destination repo from the server-owned allowlist,
                              dedupe, then file a sanitized issue. Requires HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET.
  --mcp                       Run the operator toolset as an MCP stdio server (for Claude Code, Codex,
                              and other MCP clients). No model provider key is required; gates and
                              audit follow the same HONUA_DEVOPS_* runtime controls. See docs/QUICKSTART-MCP.md.
  --await-approval <id>       Wait for a paused deploy-control operation to leave AwaitingApproval.
                              Polls the honua-server deploy-control operation until an operator approves
                              it in Console (Submitted/terminal) or HONUA_DEVOPS_APPROVAL_TIMEOUT_SECONDS
                              (default 3600) elapses. pr-first/break-glass-only wait read-only; direct-allowed
                              may submit per policy. Reports the final status and exits non-zero on timeout/error.
  -h, --help                  Show this help and exit.

Environment (--listen):
  HONUA_DEVOPS_WEBHOOK_SECRET       Required. Shared HMAC-SHA256 secret matching honua-support.
  HONUA_DEVOPS_WEBHOOK_PORT         Optional. TCP port to bind (default 8090).
  HONUA_DEVOPS_WEBHOOK_PATH         Optional. URL path to accept POSTs on (default /escalations).
  HONUA_DEVOPS_WEBHOOK_AUTO_TRIAGE  Optional. When true (default), auto-triage on receive.

Environment (--intake-listen, Enterprise only):
  HONUA_DEVOPS_INTAKE_PROVIDER        Required. `jira` (or `none`, the default = intake disabled).
  HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET  Required for jira. Shared HMAC-SHA256 webhook secret.
  HONUA_DEVOPS_INTAKE_PORT            Optional. TCP port to bind (default 8091).
  HONUA_DEVOPS_INTAKE_PATH            Optional. URL path to accept POSTs on (default /intake).
  HONUA_DEVOPS_INTAKE_ALLOWED_HOSTS   Comma-separated hosts allowed for Jira write-back.
  HONUA_DEVOPS_INTAKE_AUTO_DRAFT      Optional. Reserved; draft generation is not implemented yet.
  HONUA_DEVOPS_JIRA_BASE_URL          Jira Cloud base URL for issue read + provenance write-back.
  HONUA_DEVOPS_JIRA_API_TOKEN         Jira Cloud API token (Basic auth password).
  HONUA_DEVOPS_JIRA_USER_EMAIL        Jira Cloud account email (Basic auth username).
  HONUA_DEVOPS_JIRA_PROJECT_FILTER    Optional. Only accept issues from this project key.

Environment (--bugreport-listen):
  HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET  Required. Shared HMAC-SHA256 secret matching honua-support.
  HONUA_DEVOPS_BUGREPORT_PORT            Optional. TCP port to bind (default 8092).
  HONUA_DEVOPS_BUGREPORT_PATH            Optional. URL path to accept POSTs on (default /bug-reports).
  HONUA_DEVOPS_BUGREPORT_REPLAY_WINDOW_SECONDS  Optional. Replay/freshness window (default 300).
  HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP   Server-owned allowlist: `component=owner/repo,component2=owner/repo`.
                                         The SOLE source of the destination repo; an unmapped component is refused.
  HONUA_DEVOPS_BUGREPORT_LABELS          Optional. Comma-separated issue labels (default `bug,honua-support`).
  HONUA_DEVOPS_GITHUB_API_BASE_URL       Optional. GitHub API base (e.g. https://api.github.com). Unset = report-only.
  HONUA_DEVOPS_GITHUB_TOKEN              Optional. GitHub token for filing. Unset = report-only (plan) posture.
  HONUA_DEVOPS_BUGREPORT_ALLOWED_HOSTS   Comma-separated hosts allowed for GitHub API calls.

Examples:
  honua-devops --preflight
  honua-devops --provider codex --prompt "describe the environment"
  honua-devops --list-tools
  honua-devops --list-operations --limit 50
  honua-devops --show-operation 7d2b9f...
  honua-devops --listen
  honua-devops --intake-listen
  honua-devops --bugreport-listen
  honua-devops --mcp
  honua-devops --await-approval 7d2b9f...
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
        bool intakeListen = false;
        bool bugReportListen = false;
        bool mcp = false;
        string? awaitApproval = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals(ProviderFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"{ProviderFlag} requires a value: codex, claude, local-llama, or bedrock.");
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

            if (argument.Equals(IntakeListenFlag, StringComparison.OrdinalIgnoreCase))
            {
                intakeListen = true;
                continue;
            }

            if (argument.Equals(BugReportListenFlag, StringComparison.OrdinalIgnoreCase))
            {
                bugReportListen = true;
                continue;
            }

            if (argument.Equals(McpFlag, StringComparison.OrdinalIgnoreCase))
            {
                mcp = true;
                continue;
            }

            if (argument.Equals(AwaitApprovalFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"{AwaitApprovalFlag} requires a deploy-control operation id.");
                }

                awaitApproval = args[++index];
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
                $"Unknown argument `{argument}`. Use {ProviderFlag}, {PromptFlag}, {PreflightFlag}, {ListToolsFlag}, {ListOperationsFlag}, {ShowOperationFlag}, {LimitFlag}, {ListenFlag}, {IntakeListenFlag}, {BugReportListenFlag}, {McpFlag}, {AwaitApprovalFlag}, or {HelpFlag}.");
        }

        if (help || listTools || listOperations || showOperation is not null || mcp)
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
                listen,
                intakeListen,
                bugReportListen,
                mcp,
                awaitApproval);
        }

        string selectedProvider = providerValue ??
                                  Environment.GetEnvironmentVariable(ProviderEnvironmentVariable) ??
                                  ProviderKind.Codex.ToString();

        if (!ProviderKindExtensions.TryParse(selectedProvider, out ProviderKind provider))
        {
            throw new InvalidOperationException(
                $"Invalid provider `{selectedProvider}`. Supported values: codex, claude, local-llama, bedrock.");
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
            listen,
            intakeListen,
            bugReportListen,
            mcp,
            awaitApproval);
    }
}
