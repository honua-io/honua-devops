namespace Honua.DevOps.Agent.Operations.Actuation;

// How the remediation intent was established. The source is recorded on the response so an
// auditor can see WHICH typed input classified the request — never a guess about free text.
internal enum RemediationIntentSource
{
    // Nothing typed was supplied, or what was supplied maps to no registered actuator.
    Unresolved,

    // A server-owned deterministic ops finding id (or its bare rule id).
    FindingId,

    // An explicit registered remediation action name.
    TypedAction,

    // Compatibility path: an explicit `operationId=<id>` token naming a durable
    // deploy-control operation. Retained so existing callers keep working.
    OperationId
}

// The resolved (or deliberately unresolved) remediation intent.
//
// `Action` is null unless a REGISTERED remediation action was established from typed input.
// Nothing downstream may promote a null action into readiness: the honest answer is
// `unsupported-action` with zero backend calls.
internal sealed record RemediationIntent(
    string? Action,
    RemediationIntentSource Source,
    string? Rule,
    string RequestLabel,
    string Detail)
{
    internal bool IsResolved => !string.IsNullOrWhiteSpace(Action);
}

// Deterministic remediation intent classification (issue #156, REQ-002).
//
// The bug this replaces: `AutoRemediationPlanAsync` classified intent by substring matching
// on free text — a detected-issue description CONTAINING "drift" resolved to the drift
// actuator. That makes the actuator a function of prose, so a sentence that merely mentions
// drift, or one that describes drift without using the word, is classified wrongly, and the
// classification cannot be audited or tested against the server's own vocabulary.
//
// Intent now enters through exactly two typed doors:
//
//   1. a server-owned ops finding id  (preferred; deterministic)
//   2. an explicit registered remediation action name
//
// plus one documented compatibility door (an explicit `operationId=<id>` token) so existing
// callers that name a durable deploy-control operation keep working.
//
// FINDING ID SHAPE (honua-server, `OpsFindingId.Create`):
//
//   {rule}-{32 lowercase hex}
//
// where `{rule}` is the kebab-case rule id of the deterministic rule that produced the
// finding and the suffix is the first 128 bits of SHA-256 over `"{rule} {subjectKey}"`.
// The rule id is therefore recoverable from the id by stripping the fixed-width digest, and
// the same live condition always yields the same id. A bare rule id (the vocabulary the
// server's `honua_ops_findings` `rule` filter and this agent's observe->diagnose->propose
// loop already use) is accepted too, since it carries the same classification.
//
// The mapping is EXACT on the recovered rule id. There is no prefix, contains, or fuzzy
// match: `alert-dispatch-backlog` and `alert-dispatch-channel-failure` are different rules
// and must never collapse into each other.
internal static class RemediationIntentMap
{
    // `OpsFindingId.Create` emits 16 bytes as lowercase hex.
    private const int DigestLength = 32;

    // Server rule id -> the registered actuator this agent can honestly run for it.
    //
    // Deliberately small: it covers ONLY the two actuators in `ActuatorRegistry`. Every other
    // server rule stays unmapped until its actuator exists (issue #156 REQ-001/REQ-003), and an
    // unmapped rule resolves to `unsupported-action` with zero backend calls.
    private static readonly IReadOnlyDictionary<string, string> RuleActuators =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // A deploy operation is stuck in ManualInterventionRequired. The server's own
            // recommended action for this rule is a rollback to the prior revision, and the
            // finding subject carries the `operationId` this agent needs to actuate it.
            ["deploy-manual-intervention"] = RemediationAction.GitOpsRollback,

            // Declared platform release vs. what a plane/target actually runs. Neither rule
            // has a mutation this agent implements, so the honest action for both is the
            // read-only manifest drift report that shows desired vs. actual.
            ["platform-release-skew"] = RemediationAction.DriftObserve,
            ["platform-release-runtime-divergence"] = RemediationAction.DriftObserve
        };

    // Server rules this agent KNOWS about and deliberately does not map, with the reason.
    // Naming them keeps the refusal specific ("this rule has no actuator yet") instead of
    // generic ("unrecognized input"), and makes the vocabulary gap the epic tracks visible.
    private static readonly IReadOnlyDictionary<string, string> UnmappedRules =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alert-dispatch-backlog"] = "needs a dead-letter redrive actuator",
            ["alert-dispatch-channel-failure"] = "needs a channel-pause actuator",
            ["pending-contract-migrations"] = "contracting is an operator-sequenced migration step",
            ["gp-queue-depth"] = "needs a job-cancel or scale actuator",
            ["local-backend-substrate-incompatible"] = "the safe fix is a deployment-topology decision",
            ["db-bounded-admission-pressure"] = "needs a bounded-admission tuning actuator",
            ["serving-latency-slo-breach"] = "needs a restart, scale, or cache actuator"
        };

    // The rules that currently resolve to an actuator, for operator-facing refusal text.
    internal static IReadOnlyCollection<string> MappedRules
        => [.. RuleActuators.Keys.Order(StringComparer.Ordinal)];

    internal static IReadOnlyCollection<string> KnownUnmappedRules
        => [.. UnmappedRules.Keys.Order(StringComparer.Ordinal)];

    // Resolve the remediation intent from typed input only.
    //
    // Precedence: finding id (deterministic, server-owned) first, then an explicit action
    // name, then the `operationId` compatibility path. When a finding id and an action name
    // are both supplied and disagree, the request is AMBIGUOUS and stays unresolved — the
    // agent does not pick a winner on the caller's behalf.
    internal static RemediationIntent Resolve(string? findingId, string? actionName, string? operationId)
    {
        string? normalizedFindingId = Trim(findingId);
        string? normalizedAction = Trim(actionName);
        string? normalizedOperationId = Trim(operationId);

        string? findingAction = null;
        string? rule = null;
        if (normalizedFindingId is not null)
        {
            rule = ExtractRule(normalizedFindingId);
            if (!RuleActuators.TryGetValue(rule, out findingAction))
            {
                return Unresolved(
                    normalizedFindingId,
                    UnmappedRules.TryGetValue(rule, out string? reason)
                        ? $"Ops finding rule `{rule}` is a known server rule with no registered actuator ({reason}). {Vocabulary()}"
                        : $"Ops finding rule `{rule}` maps to no registered actuator. {Vocabulary()}");
            }
        }

        string? typedAction = null;
        if (normalizedAction is not null)
        {
            if (!ActuatorRegistry.TryResolveRemediation(normalizedAction, out ActuatorDescriptor descriptor))
            {
                return Unresolved(
                    normalizedAction,
                    $"`{normalizedAction}` is not a registered remediation action. {Vocabulary()}");
            }

            typedAction = descriptor.Action;
        }

        if (findingAction is not null && typedAction is not null &&
            !string.Equals(findingAction, typedAction, StringComparison.Ordinal))
        {
            return Unresolved(
                $"{normalizedFindingId}/{normalizedAction}",
                $"Ambiguous intent: finding rule `{rule}` maps to `{findingAction}` but the caller named " +
                $"`{typedAction}`. Supply one typed intent, not two that disagree.");
        }

        string? action = findingAction ?? typedAction;
        RemediationIntentSource source = findingAction is not null
            ? RemediationIntentSource.FindingId
            : RemediationIntentSource.TypedAction;
        string label = findingAction is not null ? normalizedFindingId! : normalizedAction ?? string.Empty;

        if (action is null)
        {
            if (normalizedOperationId is null)
            {
                return Unresolved(
                    "not classified",
                    "No typed remediation intent was supplied. Pass a server-owned `findingId`, an explicit " +
                    $"`remediationAction`, or an `operationId=<id>` token. {Vocabulary()}");
            }

            // Compatibility path: naming a durable deploy-control operation is an explicit,
            // typed request to roll that operation back. It is not inferred from prose.
            action = RemediationAction.GitOpsRollback;
            source = RemediationIntentSource.OperationId;
            label = $"operationId={normalizedOperationId}";
        }

        // A rollback actuates a NAMED durable operation. Without that identity there is
        // nothing to actuate, so the request stays unresolved rather than resolving to an
        // actuator that would then be handed an empty target.
        if (string.Equals(action, RemediationAction.GitOpsRollback, StringComparison.Ordinal) &&
            normalizedOperationId is null)
        {
            return Unresolved(
                label,
                $"`{RemediationAction.GitOpsRollback}` rolls back a NAMED durable deploy-control operation, but no " +
                "operation id was supplied. Pass the finding subject's operation id as an `operationId=<id>` token.");
        }

        return new RemediationIntent(
            Action: action,
            Source: source,
            Rule: rule,
            RequestLabel: label,
            Detail: source switch
            {
                RemediationIntentSource.FindingId =>
                    $"Server-owned finding id `{label}` (rule `{rule}`) maps to registered action `{action}`.",
                RemediationIntentSource.TypedAction =>
                    $"Caller named registered action `{action}` explicitly.",
                _ => $"Caller named durable deploy-control operation `{normalizedOperationId}` explicitly."
            });
    }

    // Recover the server rule id from a finding id by stripping the fixed-width digest
    // suffix. A value that does not carry a digest is treated as a bare rule id.
    internal static string ExtractRule(string findingId)
    {
        ArgumentNullException.ThrowIfNull(findingId);
        string value = findingId.Trim();
        if (value.Length < DigestLength + 2 || value[^(DigestLength + 1)] != '-')
        {
            return value;
        }

        ReadOnlySpan<char> digest = value.AsSpan(value.Length - DigestLength);
        foreach (char character in digest)
        {
            bool isLowerHex = character is >= '0' and <= '9' || character is >= 'a' and <= 'f';
            if (!isLowerHex)
            {
                return value;
            }
        }

        return value[..^(DigestLength + 1)];
    }

    private static RemediationIntent Unresolved(string label, string detail)
        => new(null, RemediationIntentSource.Unresolved, null, label, detail);

    private static string Vocabulary()
        => $"Registered actions: {string.Join(", ", RemediationAction.All)}. " +
            $"Mapped finding rules: {string.Join(", ", MappedRules)}.";

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
