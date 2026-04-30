namespace Honua.DevOps.Agent.Operations.GuidedFix;

internal sealed record SupportTicket(
    string TicketId,
    string Service,
    SupportSeverity Severity,
    string Environment,
    string Symptoms,
    string RequestedAction,
    string AllowedAccessMode,
    int TtlMinutes,
    bool RollbackExpected,
    string AttachedEvidence);
