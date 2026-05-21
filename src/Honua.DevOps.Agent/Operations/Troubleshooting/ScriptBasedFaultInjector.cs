using System.Diagnostics;
using System.Text;

namespace Honua.DevOps.Agent.Operations.Troubleshooting;

internal sealed class ScriptBasedFaultInjector : IFaultInjector
{
    private readonly string _scriptsBasePath;

    internal ScriptBasedFaultInjector(string scenarioId, string targetCloud, string scriptsBasePath)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
            throw new ArgumentException("ScenarioId is required.", nameof(scenarioId));
        if (string.IsNullOrWhiteSpace(targetCloud))
            throw new ArgumentException("TargetCloud is required.", nameof(targetCloud));
        if (string.IsNullOrWhiteSpace(scriptsBasePath))
            throw new ArgumentException("ScriptsBasePath is required.", nameof(scriptsBasePath));

        ValidateScenarioId(scenarioId);

        ScenarioId = scenarioId;
        TargetCloud = targetCloud;
        _scriptsBasePath = Path.GetFullPath(scriptsBasePath);
        Status = FaultInjectorStatus.Ready;
    }

    public string ScenarioId { get; }
    public string TargetCloud { get; }
    public FaultInjectorStatus Status { get; private set; }

    private static void ValidateScenarioId(string value)
    {
        if (value.Length > 64)
            throw new ArgumentException("`scenarioId` must be 64 characters or fewer.", nameof(value));

        foreach (char character in value)
        {
            bool allowed = char.IsLetterOrDigit(character) || character is '-' or '_';
            if (!allowed)
            {
                throw new ArgumentException(
                    $"`scenarioId` must contain only letters, digits, `-`, and `_`. Rejected value: `{value}`.",
                    nameof(value));
            }
        }
    }

    public Task<FaultInjectionResult> InjectAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
    {
        return ExecuteScriptAsync(FaultInjectionAction.Inject, "inject", context, cancellationToken);
    }

    public Task<FaultInjectionResult> RestoreAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
    {
        return ExecuteScriptAsync(FaultInjectionAction.Restore, "restore", context, cancellationToken);
    }

    public Task<FaultInjectionResult> VerifyInjectedAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
    {
        return ExecuteScriptAsync(FaultInjectionAction.VerifyInjected, "verify-injected", context, cancellationToken);
    }

    public Task<FaultInjectionResult> VerifyRestoredAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
    {
        return ExecuteScriptAsync(FaultInjectionAction.VerifyRestored, "verify-restored", context, cancellationToken);
    }

    internal string GetScriptPath(string action)
    {
        string candidate = Path.GetFullPath(Path.Combine(_scriptsBasePath, $"{ScenarioId}-{action}.sh"));
        string baseWithSeparator = _scriptsBasePath.EndsWith(Path.DirectorySeparatorChar)
            ? _scriptsBasePath
            : _scriptsBasePath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(baseWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resolved fault script path `{candidate}` escapes the configured scripts base directory.");
        }

        return candidate;
    }

    private async Task<FaultInjectionResult> ExecuteScriptAsync(
        FaultInjectionAction action,
        string scriptSuffix,
        FaultInjectionContext context,
        CancellationToken cancellationToken)
    {
        context.Validate();
        string scriptPath = GetScriptPath(scriptSuffix);
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (context.DryRun)
        {
            stopwatch.Stop();
            return new FaultInjectionResult(
                Success: true,
                ScenarioId: ScenarioId,
                Action: action,
                Detail: $"[DRY-RUN] Would execute: {scriptPath}",
                Duration: stopwatch.Elapsed,
                Evidence: [$"[DRY-RUN] Script: {scriptPath}", $"[DRY-RUN] Environment: {context.Environment}", $"[DRY-RUN] Region: {context.Region}"]);
        }

        if (!File.Exists(scriptPath))
        {
            stopwatch.Stop();
            Status = FaultInjectorStatus.Failed;
            return new FaultInjectionResult(
                Success: false,
                ScenarioId: ScenarioId,
                Action: action,
                Detail: $"Script not found: {scriptPath}",
                Duration: stopwatch.Elapsed,
                Evidence: [$"Expected script at: {scriptPath}"]);
        }

        List<string> evidence = [];

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "/bin/bash",
                Arguments = scriptPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            SetEnvironmentVariables(startInfo, context);

            using Process process = new() { StartInfo = startInfo };
            StringBuilder stdout = new();
            StringBuilder stderr = new();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using CancellationTokenSource timeoutCts = new(context.Timeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                throw;
            }

            stopwatch.Stop();

            string stdoutText = stdout.ToString().Trim();
            string stderrText = stderr.ToString().Trim();

            if (!string.IsNullOrEmpty(stdoutText))
                evidence.Add($"[stdout] {stdoutText}");
            if (!string.IsNullOrEmpty(stderrText))
                evidence.Add($"[stderr] {stderrText}");

            bool success = process.ExitCode == 0;

            if (success)
            {
                Status = action switch
                {
                    FaultInjectionAction.Inject => FaultInjectorStatus.Injected,
                    FaultInjectionAction.Restore => FaultInjectorStatus.Restored,
                    _ => Status
                };
            }
            else
            {
                Status = FaultInjectorStatus.Failed;
            }

            return new FaultInjectionResult(
                Success: success,
                ScenarioId: ScenarioId,
                Action: action,
                Detail: success
                    ? $"Script {scriptSuffix} completed successfully (exit code 0)"
                    : $"Script {scriptSuffix} failed with exit code {process.ExitCode}",
                Duration: stopwatch.Elapsed,
                Evidence: evidence);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Status = FaultInjectorStatus.Failed;
            evidence.Add("Operation was cancelled or timed out");
            return new FaultInjectionResult(
                Success: false,
                ScenarioId: ScenarioId,
                Action: action,
                Detail: $"Script {scriptSuffix} was cancelled or timed out after {stopwatch.Elapsed.TotalSeconds:F1}s",
                Duration: stopwatch.Elapsed,
                Evidence: evidence);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Status = FaultInjectorStatus.Failed;
            evidence.Add($"[exception] {ex.GetType().Name}: {ex.Message}");
            return new FaultInjectionResult(
                Success: false,
                ScenarioId: ScenarioId,
                Action: action,
                Detail: $"Script {scriptSuffix} failed with exception: {ex.Message}",
                Duration: stopwatch.Elapsed,
                Evidence: evidence);
        }
    }

    private static void SetEnvironmentVariables(ProcessStartInfo startInfo, FaultInjectionContext context)
    {
        startInfo.Environment["FAULT_ENV"] = context.Environment;
        startInfo.Environment["FAULT_REGION"] = context.Region;
        startInfo.Environment["FAULT_RESOURCE_PREFIX"] = context.ResourcePrefix;
        startInfo.Environment["FAULT_DRY_RUN"] = context.DryRun ? "true" : "false";
        startInfo.Environment["FAULT_TIMEOUT"] = ((int)context.Timeout.TotalSeconds).ToString();

        foreach (KeyValuePair<string, string> credential in context.Credentials)
        {
            startInfo.Environment[$"FAULT_CRED_{credential.Key.ToUpperInvariant()}"] = credential.Value;
        }
    }
}
