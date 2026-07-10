using System.ComponentModel;
using Honua.DevOps.Agent.Operations.Observability;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    [Description("Run one bounded Honua-server-owned observe, diagnose, and optional propose cycle. Reads ops health, deterministic findings, alert history, the Operate timeline, platform-release status, deploy operations, and the live supportedKinds catalog through Honua's MCP endpoint. Correlates recommendedAction and evidenceRefs without loading unbounded history. When proposeRecommendedAction=true, proposes at most one supported finding through the canonical finding-id gateway route; requires execution tier propose or higher and never reconstructs hidden payloads, approves, submits, rolls back, or bypasses server autonomy/approval policy.")]
    public Task<OpsLoopReport> ObserveDiagnoseProposeAsync(
        string findingId,
        string severity,
        string rule,
        int lookbackHours,
        int pageSize,
        bool proposeRecommendedAction,
        CancellationToken cancellationToken = default)
    {
        OpsObserveDiagnoseProposeLoop loop = new(runtime, gateway);
        return loop.RunAsync(
            findingId,
            severity,
            rule,
            lookbackHours,
            pageSize,
            proposeRecommendedAction,
            cancellationToken);
    }
}
