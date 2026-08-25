using System.Diagnostics;
using Honua.DevOps.Agent.Operations.Troubleshooting;
using Honua.DevOps.Agent.Providers;
using Microsoft.Agents.AI;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// The credentialed lane's responder: it puts the blind prompt to a live provider.
/// </summary>
/// <remarks>
/// The eval agent is created with NO operator tools. The blind evaluation measures
/// diagnostic reasoning from evidence; giving the model the actuation surface would let
/// a scheduled eval run reach a real backend.
/// </remarks>
internal sealed class AgentBlindEvalResponder : IBlindEvalResponder
{
    private readonly ChatClientAgent _agent;

    private AgentBlindEvalResponder(ChatClientAgent agent, ProviderKind provider, string modelId)
    {
        _agent = agent;
        ProviderId = provider.ToConfigValue();
        ModelId = modelId;
    }

    public string Lane => "live";

    public string ProviderId { get; }

    public string ModelId { get; }

    /// <summary>
    /// Builds the live responder from provider environment configuration. Missing
    /// credentials throw here, before any scenario runs, so the lane fails loudly
    /// instead of publishing an empty scorecard.
    /// </summary>
    internal static AgentBlindEvalResponder Create(ProviderKind provider)
    {
        ProviderConfiguration configuration = ProviderConfiguration.Load(provider);
        ChatClientAgent agent = AgentProviderFactory.Create(
            provider,
            BlindEvalPrompt.SystemPrompt,
            tools: null);

        return new AgentBlindEvalResponder(agent, provider, configuration.Model);
    }

    public async Task<BlindEvalResponse> RespondAsync(
        FaultScenario scenario,
        BlindEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string prompt = BlindEvalPrompt.RenderUserPrompt(request);

        // A fresh session per scenario: carrying context across scenarios would leak
        // one incident's diagnosis into the next one's blind prompt.
        AgentSession session = await _agent.CreateSessionAsync(cancellationToken);

        Stopwatch stopwatch = Stopwatch.StartNew();
        AgentResponse response = await _agent.RunAsync(
            prompt,
            session,
            options: null,
            cancellationToken: cancellationToken);
        stopwatch.Stop();

        return new BlindEvalResponse(response.Text ?? string.Empty, stopwatch.Elapsed);
    }
}
