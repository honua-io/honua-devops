namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed class NullAuditSink : IAuditSink
{
    public string Target => "none";

    public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
