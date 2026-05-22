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
                symptoms: payload.Symptoms ?? string.Empty,
                requestedAction: "diagnose",
                allowedAccessMode: "read-only",
                ttlMinutes: 0,
                rollbackExpected: false,
                attachedEvidence: payload.DiagnosisSummary() ?? string.Empty,
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
        builder.Append("Symptoms: ").AppendLine(payload.Symptoms ?? "(none)");

        string? summary = payload.DiagnosisSummary();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.Append("Diagnosis: ").AppendLine(summary);
        }
        else if (payload.Diagnosis.ValueKind == JsonValueKind.Object)
        {
            builder.Append("Diagnosis: ").AppendLine(payload.Diagnosis.GetRawText());
        }
        else
        {
            builder.AppendLine("Diagnosis: (none)");
        }

        string? mode = payload.DiagnosisMode();
        if (!string.IsNullOrWhiteSpace(mode))
        {
            builder.Append("Recommended mode: ").AppendLine(mode);
        }

        if (payload.EscalatedAt.HasValue)
        {
            builder.Append("Escalated at: ").AppendLine(payload.EscalatedAt.Value.ToString("u"));
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
