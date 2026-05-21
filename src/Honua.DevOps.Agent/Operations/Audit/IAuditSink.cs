namespace Honua.DevOps.Agent.Operations.Audit;

internal interface IAuditSink : IAsyncDisposable
{
    string Target { get; }

    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
