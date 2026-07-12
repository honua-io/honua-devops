namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>Creates the configured bug-report event idempotency backend.</summary>
internal static class EventIdempotencyStoreFactory
{
    internal static IEventIdempotencyStore Create(
        BugReportConfiguration configuration,
        TextWriter? warningWriter = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.IdempotencyStore == EventIdempotencyStoreKind.Memory)
        {
            warningWriter?.WriteLine(
                "bugreport-idempotency-memory: restart replay protection is disabled; use only as an explicit emergency fallback.");
            return new InMemoryEventIdempotencyStore(
                configuration.IdempotencyRetention,
                maxEntries: configuration.IdempotencyMaxEntries);
        }

        return new FileEventIdempotencyStore(
            configuration.IdempotencyFilePath,
            configuration.IdempotencyRetention,
            configuration.IdempotencyMaxEntries);
    }
}
