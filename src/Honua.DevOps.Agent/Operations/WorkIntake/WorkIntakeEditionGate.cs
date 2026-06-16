namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Enterprise edition gate for the work-intake capability (founder decision:
/// intake connectors + multi-env promotion are Enterprise, mirroring the
/// alert-channel precedent). Uses the same edition vocabulary and ranking as
/// <c>HonuaOperationsToolkit.NormalizeEdition</c>/<c>EditionRank</c>, and the
/// refusal response follows the shape of its <c>BuildEditionGateResponse</c>.
///
/// Kept as a small, transport-free static so the CLI can enforce it before the
/// listener binds and so it is unit-testable without a live server.
/// </summary>
internal static class WorkIntakeEditionGate
{
    internal const string RequiredEdition = "enterprise";
    internal const string Capability = "work-intake-listen";

    /// <summary>True when the detected edition is Enterprise or higher.</summary>
    internal static bool IsAllowed(string? detectedEdition)
        => EditionRank(Normalize(detectedEdition)) >= EditionRank(RequiredEdition);

    /// <summary>
    /// Builds the <c>edition-gated</c> refusal for the intake capability when the
    /// detected edition is below Enterprise. Same status/field shape as the
    /// toolkit's edition-gate response so callers and tests can assert uniformly.
    /// </summary>
    internal static OperationResponse BuildRefusal(string? detectedEdition)
    {
        string current = Normalize(detectedEdition);
        return new OperationResponse(
            Status: "edition-gated",
            Summary: $"Capability `{Capability}` requires `{RequiredEdition}` edition; current edition is `{current}`.",
            Findings:
            [
                $"Current edition: {current}.",
                $"Required edition: {RequiredEdition}.",
                "Work-intake connectors (Jira/ServiceNow) and multi-environment promotion are Enterprise capabilities."
            ],
            Actions:
            [
                $"Run the work-intake listener against an `{RequiredEdition}`-licensed Honua server.",
                "Keep intake plan-only (provenance stub only) until edition and approval gates are satisfied."
            ],
            ValidationChecks:
            [
                "Edition is detected from the connected server before the intake listener binds.",
                "Intake stays default-off until a provider and webhook secret are configured."
            ],
            Risks:
            [
                "Bypassing the edition gate can expose unsupported intake/promotion behavior."
            ]);
    }

    private static string Normalize(string? edition)
    {
        string resolved = string.IsNullOrWhiteSpace(edition) ? "community" : edition.Trim().ToLowerInvariant();
        return resolved switch
        {
            "community" => "community",
            "pro" => "pro",
            "professional" => "pro",
            "enterprise" => "enterprise",
            _ => "community"
        };
    }

    private static int EditionRank(string edition)
        => edition switch
        {
            "enterprise" => 3,
            "pro" => 2,
            _ => 1
        };
}
