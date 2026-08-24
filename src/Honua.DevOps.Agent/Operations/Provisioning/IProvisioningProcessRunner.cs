namespace Honua.DevOps.Agent.Operations;

internal interface IProvisioningProcessRunner
{
    /// <summary>
    /// True when this runner can actually launch <paramref name="fileName"/>. Checking up
    /// front turns a missing runtime prerequisite into an actionable refusal instead of an
    /// opaque "process did not start" failure — notably for the published MCP container,
    /// whose chiseled final image contains only the operator binary.
    /// </summary>
    bool CanRun(string fileName);

    Task<ProvisioningProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

internal sealed record ProvisioningProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    internal bool Succeeded => ExitCode == 0 && !TimedOut;
}
