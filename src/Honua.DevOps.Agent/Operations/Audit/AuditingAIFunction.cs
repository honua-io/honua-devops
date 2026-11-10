using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed record UnmarshaledToolResult(object? Value, Type? DeclaredType);

// Both hosts audit the original in-process result before wire serialization.
internal sealed class AuditingAIFunction(AIFunction innerFunction, AuditContext auditContext)
    : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        object? result;
        try
        {
            result = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ToolCallAuditor.EmitAsync(
                auditContext,
                new ToolCallRecord(Name, arguments),
                new OperationResponse(
                    Status: "error",
                    Summary: Redaction.Scrub(exception.Message),
                    Findings: [],
                    Actions: [],
                    ValidationChecks: [],
                    Risks: []),
                cancellationToken);
            throw;
        }

        UnmarshaledToolResult? original = result as UnmarshaledToolResult;
        await ToolCallAuditor.EmitAsync(
            auditContext,
            new ToolCallRecord(Name, arguments),
            original is null ? result : original.Value,
            cancellationToken);
        // Preserve SDK wire options/type, including converters, ignored fields and nulls.
        // Other functions may already return framework-special marshaled content.
        if (original is null) return result;
        if (original.DeclaredType is null) return original.Value;
        return JsonSerializer.SerializeToElement(
            original.Value, original.DeclaredType ?? typeof(object), JsonSerializerOptions);
    }
}
