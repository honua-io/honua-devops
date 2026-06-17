using System.Text.Json;

using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

internal static class ServiceBundleReconciliationPlanner
{
    // Real drift verdicts the service-state scope resolves to (issue: ServiceBundle drift was
    // a stub of "check-required"/"export-required"). These mirror the GitOps drift vocabulary.
    internal static class DriftVerdicts
    {
        internal const string DriftDetected = "drift-detected";
        internal const string NoDrift = "no-drift";
        internal const string Unreconcilable = "unreconcilable";
        internal const string Indeterminate = "drift-indeterminate";
    }

    internal static ServiceBundleReconciliationPlan Build(
        string service,
        IReadOnlyList<string> environments,
        BackendConfiguration configuration,
        JsonDocument? capabilitiesPayload,
        string capabilitiesPreview,
        JsonDocument? exportPayload,
        string exportPreview)
        => Build(
            service,
            environments,
            configuration,
            capabilitiesPayload,
            capabilitiesPreview,
            exportPayload,
            exportPreview,
            adapterWorkflows: [],
            desiredRevision: null);

    // Drift-aware overload: when the caller has resolved runtime adapters (via
    // RuntimeAdapterRegistry.ResolveMany) and a desired revision, the service-state drift scope
    // is given a REAL verdict computed from the adapters' drift capability and the control-plane
    // actual-state export vs the desired revision, instead of the old "check-required" stub.
    internal static ServiceBundleReconciliationPlan Build(
        string service,
        IReadOnlyList<string> environments,
        BackendConfiguration configuration,
        JsonDocument? capabilitiesPayload,
        string capabilitiesPreview,
        JsonDocument? exportPayload,
        string exportPreview,
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows,
        string? desiredRevision)
    {
        string serviceToken = service.Trim().ToLowerInvariant();
        string environmentSummary = string.Join(",", environments);

        (string serviceStateVerdict, string serviceStateDetail) = EvaluateServiceStateDrift(
            exportPayload,
            exportPreview,
            serviceToken,
            environments,
            adapterWorkflows,
            desiredRevision);

        List<ServiceBundleDriftScope> driftScopes =
        [
            new(
                Scope: "service-state",
                ExportSource: $"/{configuration.HonuaManifestExportPath}",
                ComparisonMode: "desired-vs-actual-service-export",
                LifecycleActions:
                [
                    "create",
                    "update",
                    "disable",
                    "delete",
                    "drift"
                ],
                EvidenceRequirements:
                [
                    "service-state-export",
                    "service-drift-read"
                ],
                DriftVerdict: serviceStateVerdict,
                DriftDetail: serviceStateDetail),
            new(
                Scope: "metadata",
                ExportSource: $"/{configuration.HonuaManifestExportPath}",
                ComparisonMode: "metadata-subset-export-diff",
                LifecycleActions:
                [
                    "metadata-update",
                    "metadata-drift"
                ],
                EvidenceRequirements:
                [
                    "metadata-apply-evidence",
                    "metadata-drift-read"
                ]),
            new(
                Scope: "capabilities",
                ExportSource: $"/{configuration.HonuaAdminCapabilitiesPath}",
                ComparisonMode: "capability-snapshot-vs-desired-surface",
                LifecycleActions:
                [
                    "feature-detect",
                    "drift"
                ],
                EvidenceRequirements:
                [
                    "capability-snapshot"
                ])
        ];

        List<ServiceBundleReconciliationOperation> operations =
        [
            new(
                Surface: "capability-discovery",
                Availability: "existing-api",
                ReadSource: $"/{configuration.HonuaAdminCapabilitiesPath}",
                WriteTarget: "read-only",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface capability-discovery",
                DiffSummary: "Compare advertised control-plane capabilities to the expected ServiceBundle surface map before writes.",
                Semantics:
                [
                    "discover supported control-plane reconciliation features",
                    "separate existing surfaces from planned surfaces"
                ],
                EvidenceRequirements:
                [
                    "capability-snapshot"
                ]),
            new(
                Surface: "service-state-export",
                Availability: "existing-export",
                ReadSource: $"/{configuration.HonuaManifestExportPath}",
                WriteTarget: "read-only",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface service-state-export",
                DiffSummary: BuildDiffSummary(
                    exportPreview,
                    serviceToken,
                    "Export current service state and compare it to the desired ServiceBundle graph."),
                Semantics:
                [
                    "export desired-vs-actual service state",
                    "provide baseline for drift comparison"
                ],
                EvidenceRequirements:
                [
                    "service-state-export",
                    "service-drift-read"
                ]),
            new(
                Surface: "metadata-subset-apply",
                Availability: "manifest-subset",
                ReadSource: $"/{configuration.HonuaManifestExportPath}",
                WriteTarget: $"/{configuration.HonuaManifestApplyPath}",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface metadata-subset-apply",
                DiffSummary: "Use manifest export as the desired-vs-actual source for metadata-only reconciliation.",
                Semantics:
                [
                    "apply metadata subset changes only",
                    "do not treat manifest apply as the full control-plane engine"
                ],
                EvidenceRequirements:
                [
                    "metadata-apply-evidence",
                    "metadata-drift-read"
                ]),
            new(
                Surface: "connections",
                Availability: ResolveAvailability(capabilitiesPayload, capabilitiesPreview, "connection"),
                ReadSource: $"/{configuration.HonuaAdminCapabilitiesPath}",
                WriteTarget: "planned control-plane connections reconciliation surface",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface connections",
                DiffSummary: "Track connection drift separately from manifest metadata so credentials and endpoints remain replay-safe.",
                Semantics:
                [
                    "create",
                    "update",
                    "disable",
                    "delete",
                    "drift"
                ],
                EvidenceRequirements:
                [
                    "connection-reconciliation-evidence"
                ]),
            new(
                Surface: "publishing-and-service-settings",
                Availability: ResolveAvailability(capabilitiesPayload, capabilitiesPreview, "publish", "service"),
                ReadSource: $"/{configuration.HonuaManifestExportPath}",
                WriteTarget: "planned control-plane publishing and service settings surface",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface publishing-and-service-settings",
                DiffSummary: "Compare published layer/service settings against the desired ServiceBundle deployment spec.",
                Semantics:
                [
                    "publish",
                    "unpublish",
                    "protocol-toggle",
                    "settings-update",
                    "drift"
                ],
                EvidenceRequirements:
                [
                    "service-publish-evidence"
                ]),
            new(
                Surface: "access-policy",
                Availability: ResolveAvailability(capabilitiesPayload, capabilitiesPreview, "policy", "access"),
                ReadSource: $"/{configuration.HonuaManifestExportPath}",
                WriteTarget: "planned control-plane access policy surface",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface access-policy",
                DiffSummary: "Detect policy drift before rollout so promotion does not advance with mismatched access rules.",
                Semantics:
                [
                    "create",
                    "update",
                    "disable",
                    "delete",
                    "drift"
                ],
                EvidenceRequirements:
                [
                    "access-policy-evidence"
                ]),
            new(
                Surface: "metadata-and-styles",
                Availability: ResolveAvailability(capabilitiesPayload, capabilitiesPreview, "style", "metadata"),
                ReadSource: $"/{configuration.HonuaManifestExportPath}",
                WriteTarget: "metadata manifest subset plus planned styles reconciliation surface",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface metadata-and-styles",
                DiffSummary: "Export metadata and style state together to isolate metadata drift from style drift.",
                Semantics:
                [
                    "metadata-update",
                    "style-update",
                    "style-drift"
                ],
                EvidenceRequirements:
                [
                    "style-reconciliation-evidence"
                ]),
            new(
                Surface: "imports-and-long-running-ops",
                Availability: ResolveAvailability(capabilitiesPayload, capabilitiesPreview, "import", "operation"),
                ReadSource: $"/{configuration.HonuaManifestExportPath}",
                WriteTarget: "planned control-plane import and long-running operation surface",
                DriftCommand: $"honua-gitops service-diff --service {serviceToken} --environments {environmentSummary} --surface imports-and-long-running-ops",
                DiffSummary: "Use exported operation handles and replay-safe imports to track long-running drift separately from config drift.",
                Semantics:
                [
                    "replay-safe-import",
                    "operation-handle-tracking",
                    "idempotent-retry",
                    "drift"
                ],
                EvidenceRequirements:
                [
                    "import-operation-evidence",
                    "operation-handle-export"
                ])
        ];

        return new ServiceBundleReconciliationPlan(
            Strategy: "manifest-plus-control-plane-reconciliation",
            IdempotencyModel: "idempotent-and-replay-safe",
            DriftModel: "desired-vs-actual-export-with-service-state-diff",
            ExportMode: "manifest-export-plus-capability-probes",
            LongRunningOperationMode: "operation-handle-tracking-and-replay-safe-retry",
            CurrentStateSummary: BuildCurrentStateSummary(exportPayload, exportPreview, serviceToken, environments),
            DriftScopes: driftScopes,
            Operations: operations);
    }

    internal static IReadOnlyList<string> FlattenEvidenceRequirements(ServiceBundleReconciliationPlan plan)
    {
        return plan.Operations
            .SelectMany(operation => operation.EvidenceRequirements)
            .Concat(plan.DriftScopes.SelectMany(scope => scope.EvidenceRequirements))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Compute a real service-state drift verdict from the runtime adapters' drift capability
    // and the control-plane actual-state export vs the desired revision:
    //   * If no adapter can detect drift / export actual state -> unreconcilable (we cannot
    //     prove drift either way through the adapter surface).
    //   * Else if the export yields no comparable actual revision for the service -> when a
    //     desired revision is known and adapters CAN export, the desired state is not reflected
    //     in actual state -> drift-detected; otherwise indeterminate (no evidence to compare).
    //   * Else compare exported actual revision(s) to the desired revision:
    //       any mismatch -> drift-detected; all match -> no-drift.
    private static (string Verdict, string Detail) EvaluateServiceStateDrift(
        JsonDocument? exportPayload,
        string exportPreview,
        string serviceToken,
        IReadOnlyList<string> environments,
        IReadOnlyList<RuntimeAdapterWorkflow> adapterWorkflows,
        string? desiredRevision)
    {
        bool adaptersCanDetect = adapterWorkflows.Any(workflow => workflow.Capability.SupportsDrift);
        bool adaptersCanExport = adapterWorkflows.Any(workflow => workflow.Capability.SupportsActualStateExport);

        // No adapter surface to compare against, and no desired revision to anchor on: the
        // service-state scope cannot be reconciled through the adapters in this request.
        if (adapterWorkflows.Count > 0 && !adaptersCanDetect && !adaptersCanExport)
        {
            return (
                DriftVerdicts.Unreconcilable,
                $"No resolved runtime adapter advertises drift detection or actual-state export for `{serviceToken}`; service-state drift cannot be reconciled through the adapter surface.");
        }

        IReadOnlyDictionary<string, string> actualRevisions = ExtractActualRevisions(exportPayload, serviceToken);
        string desired = string.IsNullOrWhiteSpace(desiredRevision) ? string.Empty : desiredRevision.Trim();

        // Restrict the comparison to the targeted environments where the export gave a revision.
        List<KeyValuePair<string, string>> comparable = actualRevisions
            .Where(pair => environments.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (comparable.Count == 0)
        {
            // No comparable actual revision was exported. If we have a concrete desired revision
            // and adapters CAN export actual state, the desired state is simply not present in
            // the actual export -> that is drift. Without a desired revision (or export
            // capability) we have nothing to compare -> indeterminate, surfaced honestly.
            if (!string.IsNullOrEmpty(desired) && adaptersCanExport)
            {
                return (
                    DriftVerdicts.DriftDetected,
                    $"Desired revision `{desired}` for `{serviceToken}` has no matching exported actual revision across `{string.Join(", ", environments)}`; treating the missing actual state as drift.");
            }

            return (
                DriftVerdicts.Indeterminate,
                string.IsNullOrEmpty(desired)
                    ? $"No desired revision supplied for `{serviceToken}`; service-state drift cannot be evaluated yet."
                    : $"No exported actual revision for `{serviceToken}`; capture an actual-state export before asserting drift.");
        }

        if (string.IsNullOrEmpty(desired))
        {
            return (
                DriftVerdicts.Indeterminate,
                $"Exported actual revisions present ({string.Join(", ", comparable.Select(pair => $"{pair.Key}={pair.Value}"))}) but no desired revision was supplied to compare against.");
        }

        string[] mismatches = comparable
            .Where(pair => !pair.Value.Equals(desired, StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        if (mismatches.Length > 0)
        {
            return (
                DriftVerdicts.DriftDetected,
                $"Desired revision `{desired}` differs from exported actual revision(s) {string.Join(", ", mismatches)} for `{serviceToken}`.");
        }

        return (
            DriftVerdicts.NoDrift,
            $"All exported actual revisions for `{serviceToken}` match desired revision `{desired}` across `{string.Join(", ", comparable.Select(pair => pair.Key))}`.");
    }

    // Read service/environment/revision triples from the control-plane manifest export. Mirrors
    // the shape GitOpsPlanner consumes (spec.deployment.* or metadata.* fallbacks).
    private static IReadOnlyDictionary<string, string> ExtractActualRevisions(
        JsonDocument? exportPayload,
        string serviceToken)
    {
        Dictionary<string, string> revisions = new(StringComparer.OrdinalIgnoreCase);
        if (exportPayload is null)
        {
            return revisions;
        }

        JsonElement root = exportPayload.RootElement;
        JsonElement resources = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("resources", out JsonElement resourcesElement))
        {
            resources = resourcesElement;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("items", out JsonElement itemsElement))
        {
            resources = itemsElement;
        }

        if (resources.ValueKind != JsonValueKind.Array)
        {
            return revisions;
        }

        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string? resourceService = ReadNestedString(resource, "spec", "deployment", "service")
                ?? ReadNestedString(resource, "metadata", "labels", "service");
            if (!string.Equals(resourceService, serviceToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? environment = ReadNestedString(resource, "spec", "deployment", "environment")
                ?? ReadNestedString(resource, "metadata", "namespace");
            string? revision = ReadNestedString(resource, "spec", "deployment", "revision")
                ?? ReadNestedString(resource, "metadata", "annotations", "honua.devops/revision");
            if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(revision))
            {
                continue;
            }

            revisions[environment] = revision;
        }

        return revisions;
    }

    private static string ResolveAvailability(JsonDocument? capabilitiesPayload, string capabilitiesPreview, params string[] probes)
    {
        if (probes.Any(probe => ContainsProbe(capabilitiesPayload?.RootElement, probe)) ||
            probes.Any(probe => capabilitiesPreview.Contains(probe, StringComparison.OrdinalIgnoreCase)))
        {
            return "capability-advertised";
        }

        return "planned-surface";
    }

    private static string BuildCurrentStateSummary(
        JsonDocument? exportPayload,
        string exportPreview,
        string serviceToken,
        IReadOnlyList<string> environments)
    {
        int matchingResources = CountMatchingResources(exportPayload, serviceToken, environments);
        if (matchingResources > 0)
        {
            return $"Current state export contains {matchingResources} matching resource(s) for `{serviceToken}` across `{string.Join(", ", environments)}`.";
        }

        if (string.IsNullOrWhiteSpace(exportPreview) || exportPreview.Equals("empty response body", StringComparison.OrdinalIgnoreCase))
        {
            return $"No current state preview was returned for `{serviceToken}`; export evidence must be captured during execution.";
        }

        return exportPreview.Contains(serviceToken, StringComparison.OrdinalIgnoreCase)
            ? $"Current state preview references `{serviceToken}` and can seed desired-vs-actual comparison."
            : $"Current state preview is present but does not clearly reference `{serviceToken}`; reconcile with explicit service-scoped export evidence.";
    }

    private static int CountMatchingResources(
        JsonDocument? exportPayload,
        string serviceToken,
        IReadOnlyList<string> environments)
    {
        if (exportPayload is null)
        {
            return 0;
        }

        JsonElement root = exportPayload.RootElement;
        JsonElement resources = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("resources", out JsonElement resourcesElement))
        {
            resources = resourcesElement;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("items", out JsonElement itemsElement))
        {
            resources = itemsElement;
        }

        if (resources.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        int matches = 0;
        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string? resourceService = ReadNestedString(resource, "spec", "deployment", "service")
                ?? ReadNestedString(resource, "metadata", "labels", "service");
            string? resourceEnvironment = ReadNestedString(resource, "spec", "deployment", "environment")
                ?? ReadNestedString(resource, "metadata", "namespace");
            if (string.Equals(resourceService, serviceToken, StringComparison.OrdinalIgnoreCase) &&
                resourceEnvironment is not null &&
                environments.Contains(resourceEnvironment, StringComparer.OrdinalIgnoreCase))
            {
                matches++;
            }
        }

        return matches;
    }

    private static bool ContainsProbe(JsonElement? element, string probe)
    {
        if (element is null)
        {
            return false;
        }

        JsonElement value = element.Value;
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property =>
                property.Name.Contains(probe, StringComparison.OrdinalIgnoreCase) ||
                ContainsProbe(property.Value, probe)),
            JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsProbe(item, probe)),
            JsonValueKind.String => value.GetString()?.Contains(probe, StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
    }

    private static string BuildDiffSummary(string exportPreview, string serviceToken, string fallback)
    {
        return exportPreview.Contains(serviceToken, StringComparison.OrdinalIgnoreCase)
            ? $"Export preview contains `{serviceToken}` and is suitable for a first desired-vs-actual diff."
            : fallback;
    }
}
