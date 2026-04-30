using System.Diagnostics;

namespace Honua.DevOps.Agent.Operations.Troubleshooting;

internal sealed record FaultInjectionReport(
    string ScenarioId,
    FaultInjectionResult InjectionResult,
    FaultInjectionResult? InjectionVerification,
    BlindEvaluationResult? EvaluationResult,
    FaultInjectionResult RestorationResult,
    FaultInjectionResult? RestorationVerification,
    TimeSpan TotalDuration)
{
    internal bool FullCycleSucceeded =>
        InjectionResult.Success &&
        InjectionVerification is { Success: true } &&
        RestorationResult.Success &&
        RestorationVerification is { Success: true };
}

internal sealed class FaultInjectionOrchestrator
{
    private readonly IFaultInjector _injector;
    private readonly FaultScenario? _scenario;

    internal FaultInjectionOrchestrator(IFaultInjector injector, FaultScenario? scenario = null)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _scenario = scenario;
    }

    internal async Task<FaultInjectionReport> ExecuteFullCycleAsync(
        FaultInjectionContext context,
        CancellationToken cancellationToken = default)
    {
        context.Validate();
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        // Step 1: Inject fault
        FaultInjectionResult injectionResult = await _injector.InjectAsync(context, cancellationToken);

        // Step 2: Verify fault is active
        FaultInjectionResult? injectionVerification = null;
        if (injectionResult.Success)
        {
            injectionVerification = await _injector.VerifyInjectedAsync(context, cancellationToken);
        }

        // Step 3: Build blind evaluation prompt (if scenario is available)
        BlindEvaluationResult? evaluationResult = null;
        if (_scenario is not null && injectionResult.Success)
        {
            // Build the blind prompt for agent evaluation
            BlindEvaluationRequest blindPrompt = BlindEvaluationHarness.BuildBlindPrompt(
                _scenario,
                EvaluationMode.ReadOnly);

            // Placeholder: agent evaluation would happen here
            // In a real run, the agent would receive the blind prompt,
            // diagnose the issue, and produce an OperationResponse.
            // For now we record the prompt was built successfully.
            _ = blindPrompt;
        }

        // Step 4: Restore the fault
        FaultInjectionResult restorationResult = await _injector.RestoreAsync(context, cancellationToken);

        // Step 5: Verify restoration
        FaultInjectionResult? restorationVerification = null;
        if (restorationResult.Success)
        {
            restorationVerification = await _injector.VerifyRestoredAsync(context, cancellationToken);
        }

        totalStopwatch.Stop();

        return new FaultInjectionReport(
            ScenarioId: _injector.ScenarioId,
            InjectionResult: injectionResult,
            InjectionVerification: injectionVerification,
            EvaluationResult: evaluationResult,
            RestorationResult: restorationResult,
            RestorationVerification: restorationVerification,
            TotalDuration: totalStopwatch.Elapsed);
    }
}
