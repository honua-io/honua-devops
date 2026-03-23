namespace Honua.DevOps.Agent.Operations.Troubleshooting;

internal enum FaultInjectorStatus
{
    Ready,
    Injected,
    Restored,
    Failed
}

internal enum FaultInjectionAction
{
    Inject,
    Restore,
    VerifyInjected,
    VerifyRestored
}

internal sealed record FaultInjectionContext(
    string Environment,
    string Region,
    string ResourcePrefix,
    Dictionary<string, string> Credentials,
    bool DryRun,
    TimeSpan Timeout)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Environment))
            throw new ArgumentException("Environment is required.", nameof(Environment));
        if (string.IsNullOrWhiteSpace(Region))
            throw new ArgumentException("Region is required.", nameof(Region));
        if (string.IsNullOrWhiteSpace(ResourcePrefix))
            throw new ArgumentException("ResourcePrefix is required.", nameof(ResourcePrefix));
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentException("Timeout must be positive.", nameof(Timeout));
    }
}

internal sealed record FaultInjectionResult(
    bool Success,
    string ScenarioId,
    FaultInjectionAction Action,
    string Detail,
    TimeSpan Duration,
    IReadOnlyList<string> Evidence);

internal interface IFaultInjector
{
    string ScenarioId { get; }
    string TargetCloud { get; }
    FaultInjectorStatus Status { get; }
    Task<FaultInjectionResult> InjectAsync(FaultInjectionContext context, CancellationToken cancellationToken = default);
    Task<FaultInjectionResult> RestoreAsync(FaultInjectionContext context, CancellationToken cancellationToken = default);
    Task<FaultInjectionResult> VerifyInjectedAsync(FaultInjectionContext context, CancellationToken cancellationToken = default);
    Task<FaultInjectionResult> VerifyRestoredAsync(FaultInjectionContext context, CancellationToken cancellationToken = default);
}
