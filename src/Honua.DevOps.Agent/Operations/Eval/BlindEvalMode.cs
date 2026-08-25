using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// CLI entry point for `--eval-blind`. Chooses the responder (live provider or local
/// fixture), runs the corpus, and maps the outcome to a process exit code.
/// </summary>
internal static class BlindEvalMode
{
    internal static async Task<int> RunAsync(
        ProviderKind provider,
        BlindEvalCliOptions options,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        IBlindEvalResponder? responder = null;

        try
        {
            responder = options.FixturePath is null
                ? AgentBlindEvalResponder.Create(provider)
                : FixtureBlindEvalResponder.FromFile(options.FixturePath);
        }
        catch (Exception exception)
        {
            // REQ-002: a configured lane that cannot start is a failure, not a skip.
            log.WriteLine(
                "blind-eval: run could not start — " + Redaction.Scrub(exception.Message));
            return BlindEvalRunner.ExitIncomplete;
        }

        try
        {
            BlindEvalRunResult result = await BlindEvalRunner.RunAsync(
                responder,
                new BlindEvalOptions(
                    FaultSet: options.FaultSet,
                    Mode: options.Mode,
                    OutputPath: options.OutputPath,
                    CommitSha: options.CommitSha,
                    PassThreshold: options.PassThreshold),
                log,
                timeProvider: null,
                cancellationToken);

            return result.ExitCode;
        }
        finally
        {
            if (responder is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
