using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations.Eval;

internal sealed record BlindEvalResponse(string RawAnswer, TimeSpan Latency);

/// <summary>
/// The provider seam for the blind evaluation lane. The live implementation talks
/// to a credentialed model; tests substitute a fixture adapter so the runner,
/// grader, scorecard, and schema validation are all deterministically testable
/// without a network call.
/// </summary>
internal interface IBlindEvalResponder
{
    /// <summary>Lane classification recorded in the scorecard (`live` or `fixture`).</summary>
    string Lane { get; }

    /// <summary>Provider id recorded in the scorecard.</summary>
    string ProviderId { get; }

    /// <summary>Model identifier recorded in the scorecard.</summary>
    string ModelId { get; }

    Task<BlindEvalResponse> RespondAsync(
        FaultScenario scenario,
        BlindEvaluationRequest request,
        CancellationToken cancellationToken);
}
