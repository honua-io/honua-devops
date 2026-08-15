using System.Text;
using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// onAccepted bridge for the work-intake listener — the work-item analogue of
/// <see cref="EscalationConsoleReporter"/>. Renders the normalized
/// <see cref="WorkItem"/> to the operator console and posts a single provenance
/// "received by honua-devops" comment back to the source ticket via the
/// connector.
///
/// Plan-only by construction: no deliverable is drafted and no status is
/// transitioned. The provenance comment is the only state mutation, and it never
/// fabricates a preview/workflow link — it reuses
/// <see cref="WorkflowLink"/>.Available=false semantics conceptually (no base
/// URL ⇒ no link is claimed).
/// </summary>
internal sealed class WorkIntakeReporter
{
    private const string Divider = "------ WORK ITEM RECEIVED ------";
    private const string EndDivider = "--------------------------------";

    private readonly IWorkItemConnector _connector;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    internal WorkIntakeReporter(
        IWorkItemConnector connector,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        _connector = connector;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
    }

    internal async Task ReportAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        WriteHeader(workItem);

        if (!_connector.IsEnabled)
        {
            _stdout.WriteLine("[intake] connector disabled (no Jira base URL) — provenance stub not posted.");
            return;
        }

        try
        {
            string message = BuildProvenanceMessage(workItem);
            BackendCallResult result = await _connector.PostProvenanceStubAsync(workItem, message, cancellationToken);
            if (result.IsSuccess)
            {
                _stdout.WriteLine($"[intake] provenance stub posted to {workItem.Provider}:{workItem.ExternalId}.");
            }
            else
            {
                _stderr.WriteLine(
                    $"warn: provenance stub not posted for {workItem.Provider}:{workItem.ExternalId}: {result.Detail} :: {result.PayloadPreview}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _stderr.WriteLine(
                $"warn: provenance stub failed for {workItem.Provider}:{workItem.ExternalId}: {exception.GetType().Name} {exception.Message}");
        }
    }

    internal static string BuildProvenanceMessage(WorkItem workItem)
        // The title is external free text lifted straight off the source ticket and can carry
        // secrets (e.g. an api_key=... fragment smuggled into a Jira summary). Scrub it before
        // it is posted back to the ticket as a durable comment — the same redaction guarantee
        // the GitOps PR body and generated manifests already apply to operator-supplied text.
        => $"Received by honua-devops — work item {workItem.Provider}:{workItem.ExternalId} "
            + $"('{Redaction.Scrub(workItem.Title)}') entered the GIS-department queue. "
            + "Status: intake (plan-only). No deliverable has been drafted yet.";

    private void WriteHeader(WorkItem workItem)
    {
        StringBuilder builder = new();
        builder.AppendLine();
        builder.AppendLine(Divider);
        builder.Append("Item: ").Append(workItem.Provider).Append(':').AppendLine(workItem.ExternalId);
        builder.Append("Title: ").AppendLine(workItem.Title);
        builder.Append("Kind: ").Append(workItem.Kind)
               .Append(" | Status: ").Append(workItem.Status)
               .Append(" | Project: ").AppendLine(workItem.Project);
        builder.Append("Requester: ").AppendLine(string.IsNullOrWhiteSpace(workItem.Requester) ? "(unknown)" : workItem.Requester);
        if (!string.IsNullOrWhiteSpace(workItem.ExternalUrl))
        {
            builder.Append("Source: ").AppendLine(workItem.ExternalUrl);
        }
        builder.AppendLine(EndDivider);
        _stdout.Write(builder.ToString());
    }
}
