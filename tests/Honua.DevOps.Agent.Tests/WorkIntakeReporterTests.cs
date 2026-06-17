using Honua.DevOps.Agent.Operations.WorkIntake;

namespace Honua.DevOps.Agent.Tests;

public sealed class WorkIntakeReporterTests
{
    [Fact]
    public void BuildProvenanceMessage_KeepsSecretsOutOfTicketComment()
    {
        // The work-item title is external free text lifted off the source ticket. A secret
        // smuggled into the Jira summary must not be echoed back into the provenance comment
        // that honua-devops posts to that ticket — mirror of the GitOps PR-body guarantee.
        WorkItem workItem = new(
            Provider: WorkItem.JiraProvider,
            ExternalId: "GIS-42",
            ExternalUrl: "https://acme.atlassian.net/rest/api/3/issue/10042",
            Title: "New layer api_key=super-secret-value token=abcdef123456",
            Kind: "Task",
            Status: "To Do",
            Project: "GIS",
            Environment: null,
            Requester: "Dana Steward");

        string message = WorkIntakeReporter.BuildProvenanceMessage(workItem);

        Assert.DoesNotContain("super-secret-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", message, StringComparison.Ordinal);
        // The redaction marker is present, proving the value was scrubbed rather than dropped.
        Assert.Contains("<redacted>", message, StringComparison.Ordinal);
        // Non-sensitive identifiers are still carried through for provenance.
        Assert.Contains("GIS-42", message, StringComparison.Ordinal);
    }
}
