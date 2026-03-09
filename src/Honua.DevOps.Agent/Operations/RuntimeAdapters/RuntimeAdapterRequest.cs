namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal sealed record RuntimeAdapterRequest(
    string Service,
    IReadOnlyList<string> Environments,
    string Revision,
    string Action,
    string ChangeSummary,
    string GitOpsTool,
    string TerraformRepository,
    string TerraformRef,
    string TerraformLocalPath,
    bool DryRun,
    ExecutionMode ExecutionMode,
    ExecutionTier ExecutionTier);
