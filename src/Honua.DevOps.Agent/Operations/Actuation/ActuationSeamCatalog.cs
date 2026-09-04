namespace Honua.DevOps.Agent.Operations.Actuation;

/// <summary>
/// Inventory of state-changing authorities that are not discovered by the BackendGateway
/// reflection gate. Each entry must name its durable identity, approval, verification, and
/// audit requirements so a new subprocess or filesystem actuator cannot be added silently.
/// </summary>
internal sealed record ActuationSeamDescriptor(
    string Route,
    string Owner,
    bool HasDurableOperation,
    bool RequiresApproval,
    bool RequiresVerification,
    bool RequiresAudit);

internal static class ActuationSeamCatalog
{
    internal static readonly IReadOnlyDictionary<string, ActuationSeamDescriptor> Routes =
        new Dictionary<string, ActuationSeamDescriptor>(StringComparer.Ordinal)
        {
            ["terraform-exact-apply"] = new(
                "terraform-exact-apply",
                "HonuaOperationsToolkit.ProvisionInfrastructureAsync",
                HasDurableOperation: true,
                RequiresApproval: true,
                RequiresVerification: true,
                RequiresAudit: true),
            ["terraform-exact-destroy"] = new(
                "terraform-exact-destroy",
                "HonuaOperationsToolkit.ProvisionInfrastructureAsync",
                HasDurableOperation: true,
                RequiresApproval: true,
                RequiresVerification: true,
                RequiresAudit: true),
            ["install-handoff"] = new(
                "install-handoff",
                "HonuaOperationsToolkit.InstallHandoffAsync",
                HasDurableOperation: true,
                RequiresApproval: false,
                RequiresVerification: true,
                RequiresAudit: true),
            ["verify-install-handoff"] = new(
                "verify-install-handoff",
                "HonuaOperationsToolkit.VerifyInstallHandoffAsync",
                HasDurableOperation: true,
                RequiresApproval: false,
                RequiresVerification: true,
                RequiresAudit: true)
        };
}
