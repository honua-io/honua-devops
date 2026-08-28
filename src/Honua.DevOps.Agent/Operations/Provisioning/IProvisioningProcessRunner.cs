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

    /// <param name="environment">
    /// Environment variables set on the child in addition to the inherited ones.
    /// This exists because the honua-iac execution substrate takes part of its
    /// contract through the environment — notably
    /// <c>HONUA_IAC_REQUIRE_APPROVAL=1</c>, which honua-devops must set itself so a
    /// missing approval fails closed inside the wrapper as well as in this process.
    /// Leaving it to ambient operator configuration would make a security control
    /// depend on how the host happened to be launched. Values are never secrets.
    /// </param>
    Task<ProvisioningProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
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
