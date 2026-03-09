namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal sealed record RuntimeAdapterCapability(
    string Target,
    string Family,
    bool SupportsInfraPlanning,
    bool SupportsInfraApply,
    bool SupportsReleasePlanning,
    bool SupportsReleaseApply,
    bool SupportsVerify,
    bool SupportsRollback,
    bool SupportsDrift,
    bool SupportsActualStateExport,
    bool RequiresOutOfBandMigrations,
    bool SupportsTrafficShifting)
{
    internal string ToSummary()
    {
        return $"{Target} ({Family}): verify={(SupportsVerify ? "yes" : "no")}, rollback={(SupportsRollback ? "yes" : "no")}, drift={(SupportsDrift ? "yes" : "no")}, traffic-shift={(SupportsTrafficShifting ? "yes" : "no")}, out-of-band-migrations={(RequiresOutOfBandMigrations ? "yes" : "no")}";
    }
}
