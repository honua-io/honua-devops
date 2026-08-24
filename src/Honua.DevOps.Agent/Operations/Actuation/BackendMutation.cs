namespace Honua.DevOps.Agent.Operations.Actuation;

// Every honua-server backend route this agent can call that MUTATES state (issue #153).
//
// Mutation is never something a gateway method decides on its own from a caller flag: each
// value here names exactly one write authority, and the corresponding BackendGateway method
// cannot be called without an ActuationSpine.MutationGrant (or, for the identity-establishing
// create, an ActuationSpine.OperationGrant) bound to that same value.
internal enum BackendMutation
{
    // POST /manifest/apply with dryRun=false — writes desired-state resources.
    // The dryRun=true form is a different, provably non-mutating route
    // (BackendGateway.PreviewManifestAsync) and is NOT reachable from here.
    ManifestApply,

    // POST /deploy/operations — persists the durable deploy-control operation record with
    // submitImmediately=false. This is the identity-establishing write that opens the
    // lifecycle; it executes nothing against the target.
    DeployOperationCreate,

    // POST /deploy/operations/{id}/submit — advances a durable operation into execution.
    DeployOperationSubmit,

    // POST /deploy/operations/{id}/rollback — returns a durable operation to its prior
    // known-good revision.
    DeployOperationRollback,

    // POST /metadata-release/operations — creates a durable metadata-release operation.
    MetadataReleaseCreate,

    // POST /ops/findings/{id}/propose — creates a server-owned proposal from a finding.
    OpsFindingPropose
}

// Classification of one BackendGateway method. `Mutation` is null for routes that are
// provably non-mutating (GETs, plan/preflight POSTs, and the dryRun-pinned manifest
// preview). BackendMutationCatalogTests reflects over BackendGateway and fails when a
// method is missing from this catalog, so a new backend call cannot silently appear
// outside the durable actuation seam.
internal sealed record BackendRouteClassification(
    string MethodName,
    BackendMutation? Mutation,
    string Rationale)
{
    internal bool Mutates => Mutation is not null;
}

internal static class BackendMutationCatalog
{
    // Keyed by BackendGateway method name. Overloads share a name and therefore a
    // classification; the architecture test asserts every overload of a mutating name
    // carries a grant parameter.
    internal static readonly IReadOnlyDictionary<string, BackendRouteClassification> Routes =
        new Dictionary<string, BackendRouteClassification>(StringComparer.Ordinal)
        {
            // ---- Mutating: reachable only with a grant minted by ActuationSpine ----
            ["ApplyManifestAsync"] = new(
                "ApplyManifestAsync",
                BackendMutation.ManifestApply,
                "POST /manifest/apply with dryRun=false writes desired-state resources."),
            ["CreateDeployOperationJsonAsync"] = new(
                "CreateDeployOperationJsonAsync",
                BackendMutation.DeployOperationCreate,
                "Persists the durable deploy-control operation record that opens the lifecycle."),
            ["SubmitDeployOperationJsonAsync"] = new(
                "SubmitDeployOperationJsonAsync",
                BackendMutation.DeployOperationSubmit,
                "Advances a durable operation into execution."),
            ["RollbackDeployOperationJsonAsync"] = new(
                "RollbackDeployOperationJsonAsync",
                BackendMutation.DeployOperationRollback,
                "Returns a durable operation to its prior known-good revision."),
            ["CreateMetadataReleaseOperationJsonAsync"] = new(
                "CreateMetadataReleaseOperationJsonAsync",
                BackendMutation.MetadataReleaseCreate,
                "Creates a durable metadata-release operation."),
            ["ProposeOpsFindingAsync"] = new(
                "ProposeOpsFindingAsync",
                BackendMutation.OpsFindingPropose,
                "Creates a server-owned proposal from an ops finding."),

            // ---- Non-mutating reads and plans ----
            ["CreateMcpOpsClient"] = new("CreateMcpOpsClient", null, "Factory; performs no call."),
            ["QueryLogsAsync"] = new("QueryLogsAsync", null, "OTEL log query."),
            ["QueryMetricsAsync"] = new("QueryMetricsAsync", null, "OTEL metric query."),
            ["RequestTroubleshootAsync"] = new("RequestTroubleshootAsync", null, "Read-only diagnostic fan-out."),
            ["RequestTuneAsync"] = new("RequestTuneAsync", null, "Read-only tuning analysis fan-out."),
            ["RequestUpgradeAsync"] = new("RequestUpgradeAsync", null, "Read-only version/capability/readiness reads."),
            ["PlanGitOpsDeployAsync"] = new(
                "PlanGitOpsDeployAsync",
                null,
                "Snapshot GETs, the deploy preflight/plan reads, and a dryRun-pinned manifest preview."),
            ["PlanGitOpsRunAsync"] = new("PlanGitOpsRunAsync", null, "Snapshot GETs only; apply is skipped."),
            ["PreviewManifestAsync"] = new(
                "PreviewManifestAsync",
                null,
                "POST /manifest/apply pinned to dryRun=true; rejects a non-dry-run request before sending."),
            ["ExportManifestSnapshotAsync"] = new("ExportManifestSnapshotAsync", null, "Manifest export GET."),
            ["GetCapabilitySnapshotAsync"] = new("GetCapabilitySnapshotAsync", null, "Admin capabilities GET."),
            ["RequestDeployPreflightAsync"] = new("RequestDeployPreflightAsync", null, "Deploy preflight GET."),
            ["PlanDeployOperationAsync"] = new(
                "PlanDeployOperationAsync",
                null,
                "POST /deploy/plan computes a plan; the server does not write from it."),
            ["GetDeployOperationAsync"] = new("GetDeployOperationAsync", null, "Deploy operation GET."),
            ["GetDeployOperationJsonAsync"] = new("GetDeployOperationJsonAsync", null, "Deploy operation GET."),
            ["GetMetadataReleaseOperationByPackageJsonAsync"] = new(
                "GetMetadataReleaseOperationByPackageJsonAsync",
                null,
                "Metadata-release operation GET."),
            ["RequestManifestDriftAsync"] = new("RequestManifestDriftAsync", null, "Manifest drift GET."),
            ["RequestManifestVersionsAsync"] = new("RequestManifestVersionsAsync", null, "Manifest versions GET."),
            ["RequestRequirementsAnalysisAsync"] = new("RequestRequirementsAnalysisAsync", null, "Read-only analysis fan-out."),
            ["RequestTopologyRecommendationAsync"] = new("RequestTopologyRecommendationAsync", null, "Read-only analysis fan-out."),
            ["ProbeHonuaAsync"] = new("ProbeHonuaAsync", null, "Readiness probe GET."),
            ["ProbeOtelAsync"] = new("ProbeOtelAsync", null, "OTEL probe GET."),
            ["BuildGitOpsManifestRequest"] = new(
                "BuildGitOpsManifestRequest",
                null,
                "Pure builder; performs no call. The dryRun flag it takes is chosen by the caller's route, not by a request flag."),
            ["BuildEndpoint"] = new("BuildEndpoint", null, "Pure URI composition; performs no call."),
            ["CombineResults"] = new("CombineResults", null, "Pure aggregation of already-issued call results."),
            ["ExtractEditionFromCapabilities"] = new(
                "ExtractEditionFromCapabilities",
                null,
                "Pure parse of an already-fetched capabilities payload."),
            ["Dispose"] = new("Dispose", null, "Releases the owned HttpClient.")
        };

    internal static IEnumerable<BackendRouteClassification> MutatingRoutes
        => Routes.Values.Where(route => route.Mutates);
}
