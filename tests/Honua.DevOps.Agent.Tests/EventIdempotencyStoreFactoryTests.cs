using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class EventIdempotencyStoreFactoryTests
{
    [Fact]
    public void Create_DefaultFileMode_ReturnsDurableStore()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"honua-devops-factory-{Guid.NewGuid():n}");
        try
        {
            BugReportConfiguration configuration = Configuration(
                EventIdempotencyStoreKind.File,
                Path.Combine(directory, "events.json"));

            IEventIdempotencyStore store = EventIdempotencyStoreFactory.Create(configuration);

            Assert.IsType<FileEventIdempotencyStore>(store);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Create_ExplicitMemoryMode_WarnsAboutRestartProtection()
    {
        using StringWriter warnings = new();
        BugReportConfiguration configuration = Configuration(
            EventIdempotencyStoreKind.Memory,
            Path.Combine(Path.GetTempPath(), "unused-event-ids.json"));

        IEventIdempotencyStore store = EventIdempotencyStoreFactory.Create(configuration, warnings);

        Assert.IsType<InMemoryEventIdempotencyStore>(store);
        Assert.Contains("restart replay protection is disabled", warnings.ToString(), StringComparison.Ordinal);
    }

    private static BugReportConfiguration Configuration(EventIdempotencyStoreKind kind, string path)
        => new(
            WebhookSecret: "secret",
            Port: BugReportConfiguration.DefaultPort,
            Path: BugReportConfiguration.DefaultPath,
            ReplayWindow: TimeSpan.FromMinutes(5),
            Allowlist: ComponentRepoAllowlist.Parse("server=honua-io/honua-server"),
            Labels: BugReportConfiguration.DefaultLabels,
            GitHubApiBaseUri: null,
            GitHubToken: null,
            AllowedHosts: Array.Empty<string>(),
            IdempotencyStore: kind,
            IdempotencyFilePath: path,
            IdempotencyRetention: TimeSpan.FromHours(1),
            IdempotencyMaxEntries: 100);
}
