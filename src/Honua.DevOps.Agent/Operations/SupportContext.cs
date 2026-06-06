namespace Honua.DevOps.Agent.Operations;

// honua-devops side of the support-context-v1 contract (honua-support
// docs/contracts/support-context-v1.schema.json). The console collects these structured
// fields at ticket-create time, honua-support persists them on the ticket, and honua-devops
// consumes them to auto-bundle telemetry and scope diagnosis. Every field except the
// schemaVersion (pinned by SupportContextSerializer) is optional, so a ticket that omits the
// whole block stays valid and the auto-bundle request degrades to the legacy { instanceUrl,
// apiKey } shape.
//
// SupportGateway.TriggerAutoBundleAsync builds the serialized auto-bundle body from this
// superset, omitting any field that is absent so honua-support never sees a fabricated value.
// ScopedKey is a secret (the read-only telemetry key) and rides the existing instanceUrl +
// key forwarding posture; it is never logged or echoed.
internal sealed record SupportContext(
    SupportContextUser? User = null,
    SupportContextTenant? Tenant = null,
    string? EnvKind = null,
    string? AppVersion = null,
    string? Commit = null,
    string? Route = null,
    IReadOnlyList<SupportContextRecentError>? RecentErrors = null,
    string? InstanceUrl = null,
    string? ScopedKey = null);

// Identity of the human or automation that filed the ticket.
internal sealed record SupportContextUser(
    string? Id = null,
    string? Email = null,
    string? DisplayName = null);

// Owning tenant/customer the affected instance belongs to.
internal sealed record SupportContextTenant(
    string? Id = null,
    string? Name = null);

// A client-observed recent error captured by the console at report time.
internal sealed record SupportContextRecentError(
    string? Timestamp = null,
    string? Message = null,
    string? CorrelationId = null,
    string? Path = null,
    int? StatusCode = null);
