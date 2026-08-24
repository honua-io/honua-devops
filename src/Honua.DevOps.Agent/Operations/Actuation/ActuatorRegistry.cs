namespace Honua.DevOps.Agent.Operations.Actuation;

// What a registered actuator does when it runs. This is a property of the IMPLEMENTATION,
// not of the request: a caller cannot turn a read into a write by asking harder.
internal enum ActuatorKind
{
    // Reads backend state. Never produces executed/applied.
    ReadOnly,

    // Performs a state mutation through the durable actuation spine.
    Mutating
}

// One registered typed actuator: the write (or read) authority for exactly one action.
internal sealed record ActuatorDescriptor(
    string ActuatorId,
    string Action,
    ActuatorKind Kind,
    string Description);

// The registry of typed actuators (issue #151).
//
// Resolution happens BEFORE any readiness status is computed. When resolution fails the
// only honest answer is `unsupported-action` with zero backend calls — an execution tier,
// `autoApply`, `confirmed=true`, an approval mode, or a natural-language request can
// authorize an actuator that exists, but none of them can bring one into existence.
internal static class ActuatorRegistry
{
    private static readonly IReadOnlyDictionary<string, ActuatorDescriptor> Runbooks =
        new Dictionary<string, ActuatorDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["deploy-preflight"] = new("honua.deploy-preflight.read", "deploy-preflight", ActuatorKind.ReadOnly, "Reads the deploy-control preflight with diagnostics."),
            ["preflight"] = new("honua.deploy-preflight.read", "deploy-preflight", ActuatorKind.ReadOnly, "Reads the deploy-control preflight with diagnostics."),
            ["manifest-drift"] = new("honua.manifest-drift.read", "manifest-drift", ActuatorKind.ReadOnly, "Reads the manifest drift report."),
            ["drift"] = new("honua.manifest-drift.read", "manifest-drift", ActuatorKind.ReadOnly, "Reads the manifest drift report."),
            ["manifest-versions"] = new("honua.manifest-versions.read", "manifest-versions", ActuatorKind.ReadOnly, "Reads recent manifest versions."),
            ["manifest-history"] = new("honua.manifest-versions.read", "manifest-versions", ActuatorKind.ReadOnly, "Reads recent manifest versions."),
            ["deploy-submit"] = new("honua.deploy-operation.submit", "deploy-submit", ActuatorKind.Mutating, "Submits a durable deploy-control operation through the actuation spine."),
            ["deploy-rollback"] = new("honua.deploy-operation.rollback", "deploy-rollback", ActuatorKind.Mutating, "Rolls a durable deploy-control operation back through the actuation spine."),
            ["rollback"] = new("honua.deploy-operation.rollback", "deploy-rollback", ActuatorKind.Mutating, "Rolls a durable deploy-control operation back through the actuation spine.")
        };

    // Remediation actions this agent can actually perform. The set is deliberately small:
    // an unimplemented remediation must report `unsupported-action`, not readiness.
    private static readonly IReadOnlyDictionary<string, ActuatorDescriptor> Remediations =
        new Dictionary<string, ActuatorDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            [RemediationAction.GitOpsRollback] = new(
                "honua.deploy-operation.rollback",
                RemediationAction.GitOpsRollback,
                ActuatorKind.Mutating,
                "Rolls a named durable deploy-control operation back to its prior known-good revision."),
            [RemediationAction.DriftObserve] = new(
                "honua.manifest-drift.read",
                RemediationAction.DriftObserve,
                ActuatorKind.ReadOnly,
                "Reads manifest drift so the operator can decide on a governed correction.")
        };

    internal static IReadOnlyCollection<ActuatorDescriptor> RegisteredRunbooks
        => [.. Runbooks.Values.DistinctBy(descriptor => descriptor.ActuatorId)];

    internal static IReadOnlyCollection<string> RegisteredRunbookNames => [.. Runbooks.Keys];

    internal static bool TryResolveRunbook(string? runbookName, out ActuatorDescriptor descriptor)
    {
        descriptor = null!;
        return !string.IsNullOrWhiteSpace(runbookName)
            && Runbooks.TryGetValue(runbookName.Trim(), out descriptor!);
    }

    internal static bool TryResolveRemediation(string? action, out ActuatorDescriptor descriptor)
    {
        descriptor = null!;
        return !string.IsNullOrWhiteSpace(action)
            && Remediations.TryGetValue(action.Trim(), out descriptor!);
    }
}

// The remediation actions the agent implements. Anything outside this set resolves to no
// actuator, which is reported as `unsupported-action`.
internal static class RemediationAction
{
    internal const string GitOpsRollback = "gitops-rollback";
    internal const string DriftObserve = "drift-observe";
}
