using System.Globalization;
using Honua.DevOps.Agent.Operations.Troubleshooting;
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
    string? AwaitApproval,
    BlindEvalCliOptions? EvalBlind)
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
    private const string EvalBlindFlag = "--eval-blind";
    private const string EvalFaultSetFlag = "--eval-fault-set";
    private const string EvalModeFlag = "--eval-mode";
    private const string EvalOutputFlag = "--eval-output";
    private const string EvalCommitFlag = "--eval-commit";
    private const string EvalPassThresholdFlag = "--eval-pass-threshold";
    private const string EvalFixtureFlag = "--eval-fixture";
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
  --eval-blind                Run the blind fault-injection evaluation corpus against the configured
                              provider, score it with the DiagnosisScorecard harness, and write a
                              schema-validated scorecard artifact. Exits 0 = pass, 1 = fail,
                              2 = run could not complete. Never skips. See docs/multi-model-operator-evals.md.
  --eval-fault-set <set>      Fault set to replay: `smoke` (default, 6 scenarios), `all`,
                              `category:<fault-category>`, or a comma-separated scenario id list.
  --eval-mode <mode>          Evaluation mode: read-only (default), guided-write, execute-lower-env.
  --eval-output <path>        Scorecard artifact path (default artifacts/blind-eval/scorecard.json).
  --eval-commit <sha>         Commit SHA to pin the scorecard to. Defaults to HONUA_DEVOPS_EVAL_COMMIT_SHA,
                              then GITHUB_SHA, then `unknown`.
  --eval-pass-threshold <r>   Aggregate pass-rate threshold in [0,1] (default 0.80).
  --eval-fixture <path>       Answer from a local fixture file instead of a provider. Records
                              lane=`fixture` in the scorecard: contract evidence, never model evidence.
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
  honua-devops --eval-blind --provider bedrock --eval-fault-set smoke --eval-output scorecard.json
  honua-devops --eval-blind --eval-fixture eval/fixtures/blind-eval/known-bad-answers.json
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
        bool evalBlind = false;
        string? evalFaultSet = null;
        string? evalMode = null;
        string? evalOutput = null;
        string? evalCommit = null;
        string? evalPassThreshold = null;
        string? evalFixture = null;

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

            if (argument.Equals(EvalBlindFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalBlind = true;
                continue;
            }

            if (argument.Equals(EvalFaultSetFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalFaultSet = RequireValue(args, ref index, EvalFaultSetFlag, "a fault set selector");
                continue;
            }

            if (argument.Equals(EvalModeFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalMode = RequireValue(args, ref index, EvalModeFlag, "read-only, guided-write, or execute-lower-env");
                continue;
            }

            if (argument.Equals(EvalOutputFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalOutput = RequireValue(args, ref index, EvalOutputFlag, "an output file path");
                continue;
            }

            if (argument.Equals(EvalCommitFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalCommit = RequireValue(args, ref index, EvalCommitFlag, "a commit SHA");
                continue;
            }

            if (argument.Equals(EvalPassThresholdFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalPassThreshold = RequireValue(args, ref index, EvalPassThresholdFlag, "a pass rate between 0 and 1");
                continue;
            }

            if (argument.Equals(EvalFixtureFlag, StringComparison.OrdinalIgnoreCase))
            {
                evalFixture = RequireValue(args, ref index, EvalFixtureFlag, "a fixture file path");
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
                $"Unknown argument `{argument}`. Use {ProviderFlag}, {PromptFlag}, {PreflightFlag}, {ListToolsFlag}, {ListOperationsFlag}, {ShowOperationFlag}, {LimitFlag}, {ListenFlag}, {IntakeListenFlag}, {BugReportListenFlag}, {McpFlag}, {AwaitApprovalFlag}, {EvalBlindFlag}, or {HelpFlag}.");
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
                awaitApproval,
                EvalBlind: null);
        }

        string selectedProvider = providerValue ??
                                  Environment.GetEnvironmentVariable(ProviderEnvironmentVariable) ??
                                  ProviderKind.Codex.ToString();

        if (!ProviderKindExtensions.TryParse(selectedProvider, out ProviderKind provider))
        {
            throw new InvalidOperationException(
                $"Invalid provider `{selectedProvider}`. Supported values: codex, claude, local-llama, bedrock.");
        }

        BlindEvalCliOptions? blindEval = evalBlind
            ? BlindEvalCliOptions.Resolve(evalFaultSet, evalMode, evalOutput, evalCommit, evalPassThreshold, evalFixture)
            : null;

        if (!evalBlind && (evalFaultSet is not null || evalMode is not null || evalOutput is not null
                           || evalCommit is not null || evalPassThreshold is not null || evalFixture is not null))
        {
            throw new InvalidOperationException(
                $"The --eval-* options require {EvalBlindFlag}.");
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
            awaitApproval,
            blindEval);
    }

    private static string RequireValue(string[] args, ref int index, string flag, string expectation)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{flag} requires {expectation}.");
        }

        return args[++index];
    }
}

/// <summary>
/// Resolved options for the `--eval-blind` lane. Defaults fall back to environment
/// variables so a scheduled workflow can configure the lane without threading flags
/// through every step, and so the commit SHA can be supplied by CI (the binary has no
/// way to know which commit it was built from).
/// </summary>
internal sealed record BlindEvalCliOptions(
    string FaultSet,
    EvaluationMode Mode,
    string OutputPath,
    string CommitSha,
    double PassThreshold,
    string? FixturePath)
{
    private const string EvalFaultSetEnvironmentVariable = "HONUA_DEVOPS_EVAL_FAULT_SET";
    private const string EvalOutputEnvironmentVariable = "HONUA_DEVOPS_EVAL_OUTPUT";
    private const string EvalCommitEnvironmentVariable = "HONUA_DEVOPS_EVAL_COMMIT_SHA";
    private const string EvalPassThresholdEnvironmentVariable = "HONUA_DEVOPS_EVAL_PASS_THRESHOLD";
    private const string GitHubShaEnvironmentVariable = "GITHUB_SHA";

    internal const string DefaultFaultSet = "smoke";
    internal const string DefaultOutputPath = "artifacts/blind-eval/scorecard.json";
    internal const string UnknownCommitSha = "unknown";
    internal const double DefaultPassThreshold = 0.80;

    internal static BlindEvalCliOptions Resolve(
        string? faultSet,
        string? mode,
        string? outputPath,
        string? commitSha,
        string? passThreshold,
        string? fixturePath)
    {
        string resolvedFaultSet = FirstNonEmpty(
            faultSet,
            Environment.GetEnvironmentVariable(EvalFaultSetEnvironmentVariable),
            DefaultFaultSet);

        string resolvedOutput = FirstNonEmpty(
            outputPath,
            Environment.GetEnvironmentVariable(EvalOutputEnvironmentVariable),
            DefaultOutputPath);

        string resolvedCommit = FirstNonEmpty(
            commitSha,
            Environment.GetEnvironmentVariable(EvalCommitEnvironmentVariable),
            Environment.GetEnvironmentVariable(GitHubShaEnvironmentVariable),
            UnknownCommitSha);

        string resolvedThreshold = FirstNonEmpty(
            passThreshold,
            Environment.GetEnvironmentVariable(EvalPassThresholdEnvironmentVariable),
            DefaultPassThreshold.ToString("0.00", CultureInfo.InvariantCulture));

        if (!double.TryParse(resolvedThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
            || threshold < 0
            || threshold > 1)
        {
            throw new InvalidOperationException(
                $"--eval-pass-threshold requires a number between 0 and 1; got `{resolvedThreshold}`.");
        }

        EvaluationMode resolvedMode = ParseMode(mode);

        return new BlindEvalCliOptions(
            FaultSet: resolvedFaultSet,
            Mode: resolvedMode,
            OutputPath: resolvedOutput,
            CommitSha: resolvedCommit,
            PassThreshold: threshold,
            FixturePath: string.IsNullOrWhiteSpace(fixturePath) ? null : fixturePath.Trim());
    }

    private static EvaluationMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return EvaluationMode.ReadOnly;
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "read-only" or "readonly" => EvaluationMode.ReadOnly,
            "guided-write" or "guidedwrite" => EvaluationMode.GuidedWrite,
            "execute-lower-env" or "executelowerenv" => EvaluationMode.ExecuteLowerEnv,
            _ => throw new InvalidOperationException(
                $"Invalid --eval-mode `{mode}`. Supported values: read-only, guided-write, execute-lower-env.")
        };
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }
}
