namespace Honua.DevOps.Agent.Operations;

internal interface IProvisioningProcessRunner
{
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
