using System.Globalization;

namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

/// <summary>
/// Per-ENVIRONMENT Azure geoprocessing (GP) substrate configuration: the durable, infrequently
/// changed inputs that stand up / update the GP capability on the Azure single-pool substrate.
/// This is the input the <see cref="AzureGpRuntimeAdapter"/> provisions through terraform
/// (GitOps-gated), NOT a per-job profile. It describes the substrate as a whole — the Azure
/// Batch account + a SINGLE worker pool (VM size + node ceiling), the GDAL worker container
/// image, the ACR that holds it, and the workload identity the tasks run as.
///
/// CRITICAL — Azure sizes per-POOL, not per-task, and the MVP is a SINGLE pool. There is
/// therefore NO tier selector and NO tiers variable (the AWS adapter's s/m/l/xl pool has no
/// Azure analogue yet — that is the deferred fast-follow when the iac adds multiple pools).
/// Per-job sizing is the narrow set of overrides Azure Batch actually supports per task
/// (timeout / retry / image / env); see <see cref="AzureGpResourceProfile"/> /
/// <see cref="AzureGpSizingHint"/>. The adapter binds its post-provision behaviour to the
/// substrate OUTPUTS (see <see cref="AzureGpSubstrateOutputs"/>), never to input-variable names.
/// </summary>
internal sealed record AzureGpSubstrateConfig(
    string? Image = null,
    string AcrName = "",
    string PoolVmSize = "Standard_D4s_v5",
    int MaxNodes = 8,
    string WorkloadId = "geoprocessing-batch",
    bool CreateWorkerGdalAcr = true)
{
    /// <summary>A conservative default per-env Azure GP substrate (single pool, Standard_D4s_v5, 8 nodes).</summary>
    internal static AzureGpSubstrateConfig Default { get; } = new();

    /// <summary>
    /// Render the per-ENV substrate <c>-var</c> inputs for the Azure GP substrate stack. These are
    /// substrate-shaped (pool VM size + node ceiling, create-ACR flag) — NOT a per-job profile.
    ///
    /// CONTRACT: every emitted variable name MUST be a variable the honua-iac <c>azure-gp</c>
    /// substrate STACK the adapter applies (<c>examples/azure-cert</c>, branch
    /// feat/azure-gp-substrate) actually declares as a passthrough. The example hardcodes
    /// <c>enable_azure_gp_substrate = true</c> (it is NOT a passthrough var) and exposes only the
    /// pool VM size, node ceiling, and the create-ACR flag; emitting any other name (worker image,
    /// ACR name, workload id, the gate itself) fails terraform with "Value for undeclared
    /// variable". There is intentionally NO pool-tier variable: the MVP substrate is a SINGLE pool.
    /// The cross-seam test in AzureGpAdapterContractTests guards this against the pushed iac.
    ///
    /// The worker image, ACR name, and workload id remain part of the adapter's substrate config
    /// (they describe the substrate and surface in the proposal findings + the OUTPUT bindings),
    /// but they are NOT terraform inputs of the azure-cert stack: the image is a structured
    /// <c>gp_pool_image_reference</c> the module owns, the ACR name is module-derived, and the
    /// workload id is a server-side registration, not an iac variable.
    /// </summary>
    internal IReadOnlyList<AzureGpSubstrateVar> ToSubstrateVars()
    {
        return
        [
            new AzureGpSubstrateVar(PoolVmSizeVar, PoolVmSize),
            new AzureGpSubstrateVar(MaxNodesVar, MaxNodes.ToString(CultureInfo.InvariantCulture)),
            new AzureGpSubstrateVar(CreateWorkerGdalAcrVar, CreateWorkerGdalAcr ? "true" : "false")
        ];
    }

    // Per-ENV Azure substrate input variable names. These are the EXACT passthrough variable names
    // the honua-iac azure-cert substrate stack declares (examples/azure-cert/variables.tf on
    // feat/azure-gp-substrate, wired into modules/azure-gp). The adapter does NOT bind its
    // post-provision behaviour to input-variable names — it binds to the substrate OUTPUTS (see
    // AzureGpSubstrateOutputs). Per-task knobs (timeout/retry/image/env) are deliberately ABSENT:
    // those are per-task overrides at submit time, never terraform inputs. There is deliberately NO
    // pool-tier var (single-pool MVP). Drift is guarded by AzureGpAdapterContractTests.
    //
    // The substrate feature gate (enable_azure_gp_substrate) is hardcoded `true` inside the
    // azure-cert example, so the adapter does NOT pass it as a -var; EnableVar is kept as the
    // module gate name for the proposal/validation findings only.
    internal const string EnableVar = "enable_azure_gp_substrate";
    internal const string PoolVmSizeVar = "gp_pool_vm_size";
    internal const string MaxNodesVar = "gp_pool_max_nodes";
    internal const string CreateWorkerGdalAcrVar = "create_worker_gdal_acr";
}

/// <summary>One rendered per-ENV Azure substrate terraform input (variable name + value).</summary>
internal sealed record AzureGpSubstrateVar(string Name, string Value);

/// <summary>
/// The durable Azure GP substrate the adapter provisions, addressed by its terraform OUTPUTS.
/// The contract binds to OUTPUTS (the honua-iac azure-gp module on feat/azure-gp-substrate), NOT
/// to input-variable names — the same brittle-coupling lesson the AWS adapter learned (#108). The
/// server consumes these to submit tasks against the single worker pool (account url + pool id +
/// task identity + ACR + output container), and the control-plane backend name matches the
/// honua-server <c>AzureBatchComputeBackend.BackendIdentifier</c> (<c>honua-azure-batch</c>).
/// </summary>
internal static class AzureGpSubstrateOutputs
{
    /// <summary>Output: the Azure Batch account endpoint URL the server submits tasks against.</summary>
    internal const string BatchAccountUrl = "gp_batch_account_url";

    /// <summary>Output: the single worker pool id (the server's <c>azure.batch.pool_id</c>).</summary>
    internal const string PoolId = "gp_pool_id";

    /// <summary>Output: the Azure Batch account resource id.</summary>
    internal const string BatchAccountId = "gp_batch_account_id";

    /// <summary>Output: the user-assigned managed identity resource id the tasks run as.</summary>
    internal const string TaskIdentityId = "gp_task_identity_id";

    /// <summary>Output: the principal (object) id of the task identity (for role assignments).</summary>
    internal const string TaskIdentityPrincipalId = "gp_task_identity_principal_id";

    /// <summary>Output: the ACR login server that holds the GDAL worker image.</summary>
    internal const string AcrLoginServer = "gp_acr_login_server";

    /// <summary>Output: the blob container URL where task outputs are written (azure.storage.output_container_url).</summary>
    internal const string OutputContainerUrl = "gp_output_container_url";

    /// <summary>Output: the control-plane backend name (= honua-server AzureBatchComputeBackend, <c>honua-azure-batch</c>).</summary>
    internal const string ControlPlaneBackendName = "gp_control_plane_backend_name";

    /// <summary>
    /// The control-plane backend identifier the substrate output is expected to carry; it MUST
    /// match honua-server <c>AzureBatchComputeBackend.BackendIdentifier</c>.
    /// </summary>
    internal const string ControlPlaneBackendIdentifier = "honua-azure-batch";

    /// <summary>The full ordered set of substrate outputs the adapter binds to and verifies.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        BatchAccountUrl,
        PoolId,
        BatchAccountId,
        TaskIdentityId,
        TaskIdentityPrincipalId,
        AcrLoginServer,
        OutputContainerUrl,
        ControlPlaneBackendName
    ];
}
