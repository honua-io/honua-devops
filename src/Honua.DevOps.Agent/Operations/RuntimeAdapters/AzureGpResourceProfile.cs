using System.Globalization;

namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// A pure RUNTIME SIZING HINT for a single Azure geoprocessing job. UNLIKE the AWS adapter, Azure
/// sizes per-POOL (not per-task), and the single-pool MVP has NO tier selector to build: the pool
/// (and therefore vCPU/memory) is fixed by the substrate. The runtime hint only sets the constant
/// <c>azure.batch.pool_id</c> (resolved from the substrate output) plus the narrow per-task
/// overrides Azure Batch actually supports — task timeout, max retry count, container image, and
/// task environment.
///
/// CRITICAL design (mirrors the AWS GP adapter's #108 contract fix): the override KEYS here MUST
/// be the EXACT <c>azure.batch.*</c> / <c>azure.storage.*</c> keys the honua-server
/// <c>AzureBatchComputeBackend</c> reads. NONE of this touches terraform: the durable substrate
/// (Batch account, the single worker pool, task identity, ACR, output container) is provisioned
/// ONCE per environment by <see cref="AzureGpRuntimeAdapter"/>; per-job sizing is a pure function
/// the server applies at task-add time. There is NO per-job terraform run and NO pool-tier pick.
/// </summary>
internal sealed record AzureGpResourceProfile(
    int TaskTimeoutMinutes = 60,
    int MaxTaskRetryCount = 1,
    string? ContainerImage = null,
    string WorkloadId = "geoprocessing-batch")
{
    /// <summary>Azure Batch maxTaskRetryCount limit (0..100); 0 means no retry.</summary>
    internal const int MaxRetryCeiling = 100;

    /// <summary>
    /// A conservative, valid baseline (60-minute task timeout, 1 retry). Used as the runtime
    /// default sizing hint when a job carries no explicit envelope.
    /// </summary>
    internal static AzureGpResourceProfile Baseline { get; } = new();

    /// <summary>
    /// Produce the runtime sizing hint for this job against the single-pool substrate: the
    /// constant <c>azure.batch.pool_id</c> (from the substrate output, supplied by the caller)
    /// plus the per-task overrides the server applies. Returns an invalid hint (with errors) if
    /// the timeout or retry count are out of Azure Batch bounds. There is no tier selection and no
    /// vCPU/memory override: those are fixed by the pool the substrate provisioned.
    /// </summary>
    /// <param name="poolId">
    /// The pool id resolved from the substrate output (<see cref="AzureGpSubstrateOutputs.PoolId"/>).
    /// Empty means the caller has not yet resolved the substrate output; the pool_id override is
    /// then omitted (the server falls back to its registered default pool).
    /// </param>
    internal AzureGpSizingHint ToSizingHint(string? poolId = null)
    {
        List<string> errors = [];
        List<string> notes = [];

        if (TaskTimeoutMinutes <= 0)
        {
            errors.Add("task_timeout_minutes must be greater than 0.");
        }

        if (MaxTaskRetryCount < 0 || MaxTaskRetryCount > MaxRetryCeiling)
        {
            errors.Add($"max_task_retry_count must be between 0 and {MaxRetryCeiling} (Azure Batch limit).");
        }

        // Per-task overrides the server consumes. These key strings MUST match the EXACT keys the
        // server reads in honua-server AzureBatchComputeBackend
        // (src/Honua.Server/Features/ControlPlane/AzureBatchComputeBackend.cs):
        //   azure.batch.pool_id, azure.batch.container_image,
        //   azure.batch.task_timeout_minutes, azure.batch.max_task_retry_count.
        // pool_id is the single-pool constant (no tier selection on Azure MVP); it is omitted when
        // the substrate output has not been resolved. container_image is omitted when null/blank
        // (the server uses the pool's default image). Drift from these keys is guarded by
        // AzureGpAdapterContractTests.
        Dictionary<string, string> overrides = new(StringComparer.Ordinal)
        {
            ["azure.batch.task_timeout_minutes"] = TaskTimeoutMinutes.ToString(CultureInfo.InvariantCulture),
            ["azure.batch.max_task_retry_count"] = MaxTaskRetryCount.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(poolId))
        {
            overrides["azure.batch.pool_id"] = poolId.Trim();
        }
        else
        {
            notes.Add(
                "azure.batch.pool_id was not supplied: resolve it from the substrate output " +
                $"`{AzureGpSubstrateOutputs.PoolId}` so the task targets the provisioned single pool. " +
                "Without it the server falls back to its registered default pool.");
        }

        if (!string.IsNullOrWhiteSpace(ContainerImage))
        {
            overrides["azure.batch.container_image"] = ContainerImage.Trim();
        }

        return new AzureGpSizingHint(
            IsValid: errors.Count == 0,
            PoolId: string.IsNullOrWhiteSpace(poolId) ? null : poolId.Trim(),
            SubmitTaskOverrides: overrides,
            WorkloadId: WorkloadId,
            Errors: errors,
            Notes: notes);
    }
}

/// <summary>
/// The runtime sizing hint for a single Azure GP job: the constant single-pool id and the loose
/// <c>azure.batch.*</c> per-task overrides the server applies. Pure data — no terraform, no infra
/// mutation, no pool-tier selection (single-pool MVP).
/// </summary>
internal sealed record AzureGpSizingHint(
    bool IsValid,
    string? PoolId,
    IReadOnlyDictionary<string, string> SubmitTaskOverrides,
    string WorkloadId,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes);
