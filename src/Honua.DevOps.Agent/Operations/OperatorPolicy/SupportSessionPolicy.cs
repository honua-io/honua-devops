namespace Honua.DevOps.Agent.Operations.OperatorPolicy;

internal sealed record SupportSessionPolicy(
    SupportSessionAccess Access,
    int TtlMinutes,
    bool CustomerVisible);
