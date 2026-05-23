using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Renders a received escalation webhook to the operator's console and, if
/// configured, invokes the existing read-only triage path so the operator
/// sees a diagnosis snapshot alongside the raw event.
/// </summary>
internal sealed class EscalationConsoleReporter
{
    private const string Divider = "------ ESCALATION RECEIVED ------";
    private const string EndDivider = "---------------------------------";

    private readonly HonuaOperationsToolkit? _toolkit;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    internal EscalationConsoleReporter(
        HonuaOperationsToolkit? toolkit,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        _toolkit = toolkit;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
    }

    internal async Task ReportAsync(EscalationWebhookPayload payload, CancellationToken cancellationToken)
    {
        WriteHeader(payload);

        if (_toolkit is null)
        {
            return;
        }

        try
        {
            OperationResponse triage = await _toolkit.TriageSupportTicketAsync(
                ticketId: payload.TicketId ?? string.Empty,
                severity: payload.Severity ?? "medium",
                environment: payload.Environment ?? "unknown",
                // honua-support's notification body does not carry symptoms or a
                // diagnosis blob — those live on the ticket itself. Pass empty
                // strings and let the operator follow `ticketUrl` for detail.
                symptoms: string.Empty,
                requestedAction: "diagnose",
                allowedAccessMode: "read-only",
                ttlMinutes: 0,
                rollbackExpected: false,
                attachedEvidence: string.Empty,
                cancellationToken: cancellationToken);

            WriteTriage(triage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _stderr.WriteLine($"warn: auto-triage failed for ticket {payload.TicketId}: {exception.GetType().Name} {exception.Message}");
        }
    }

    private void WriteHeader(EscalationWebhookPayload payload)
    {
        StringBuilder builder = new();
        builder.AppendLine();
        builder.AppendLine(Divider);
        builder.Append("Ticket: ").AppendLine(payload.TicketId ?? "(missing)");
        builder.Append("Customer: ").Append(payload.CustomerId ?? "(missing)")
               .Append(" | Severity: ").Append(payload.Severity ?? "(missing)")
               .Append(" | Env: ").Append(payload.Environment ?? "(missing)")
               .Append(" | Service: ").AppendLine(payload.Service ?? "(missing)");
        builder.Append("Phase: ").Append(payload.Phase ?? "(missing)")
               .Append(" | Customer status: ").AppendLine(payload.CustomerStatus ?? "(missing)");

        string? slaSummary = payload.SlaSummary();
        if (!string.IsNullOrWhiteSpace(slaSummary))
        {
            builder.Append("SLA: ").AppendLine(slaSummary);
        }
        else if (payload.Sla.ValueKind == JsonValueKind.Object)
        {
            builder.Append("SLA: ").AppendLine(payload.Sla.GetRawText());
        }

        if (payload.Escalation is { } escalation)
        {
            builder.Append("Escalation: tier=")
                   .Append(escalation.Tier?.ToString() ?? "(none)")
                   .Append(" | access-mode=")
                   .AppendLine(escalation.AccessMode ?? "(none)");

            if (escalation.EscalatedAt.HasValue)
            {
                builder.Append("Escalated at: ").AppendLine(escalation.EscalatedAt.Value.ToString("u"));
            }
        }

        if (!string.IsNullOrWhiteSpace(payload.TicketUrl))
        {
            builder.Append("Ticket URL: ").AppendLine(payload.TicketUrl);
        }

        builder.AppendLine(EndDivider);
        _stdout.Write(builder.ToString());
    }

    private void WriteTriage(OperationResponse triage)
    {
        StringBuilder builder = new();
        builder.AppendLine("[triage] status=" + triage.Status);
        builder.AppendLine("[triage] summary: " + triage.Summary);
        foreach (string finding in triage.Findings)
        {
            builder.Append("[triage]   finding: ").AppendLine(finding);
        }
        foreach (string action in triage.Actions)
        {
            builder.Append("[triage]   action: ").AppendLine(action);
        }
        _stdout.Write(builder.ToString());
    }
}
