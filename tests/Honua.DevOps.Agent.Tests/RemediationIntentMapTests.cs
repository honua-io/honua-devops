using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Tests;

// Deterministic remediation intent classification (issue #156, REQ-002).
//
// The behaviour these lock down: intent is a function of TYPED input — a server-owned ops
// finding id, an explicit registered action name, or an explicit operation id — and never of
// the prose in the detected-issue/desired-outcome text. The predecessor classified by
// substring ("does the description contain 'drift'?"), which made the actuator a function of
// wording and could not be reconciled with the server's own rule vocabulary.
public class RemediationIntentMapTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef";

    // ---- Finding id -> actuator, one row per mapped rule family ----

    [Theory]
    [InlineData("deploy-manual-intervention", RemediationAction.GitOpsRollback)]
    [InlineData("platform-release-skew", RemediationAction.DriftObserve)]
    [InlineData("platform-release-runtime-divergence", RemediationAction.DriftObserve)]
    public void MappedFindingRuleResolvesToItsRegisteredActuator(string rule, string expectedAction)
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(
            findingId: $"{rule}-{Digest}",
            actionName: null,
            operationId: "deploy-123");

        Assert.True(intent.IsResolved);
        Assert.Equal(expectedAction, intent.Action);
        Assert.Equal(RemediationIntentSource.FindingId, intent.Source);
        Assert.Equal(rule, intent.Rule);
        Assert.True(ActuatorRegistry.TryResolveRemediation(intent.Action, out ActuatorDescriptor descriptor));
        Assert.Equal(expectedAction, descriptor.Action);
    }

    // The server derives a finding id as `{rule}-{32 lowercase hex}` (OpsFindingId.Create), so
    // the rule id is recoverable from the id itself. A bare rule id — the vocabulary the
    // server's `rule` filter uses — carries the same classification.
    [Theory]
    [InlineData("platform-release-skew-0123456789abcdef0123456789abcdef", "platform-release-skew")]
    [InlineData("platform-release-skew", "platform-release-skew")]
    [InlineData("deploy-manual-intervention-fedcba9876543210fedcba9876543210", "deploy-manual-intervention")]
    // A short id, a wrong-width digest, or a non-hex suffix is not a digest: the whole value
    // is then treated as the rule id rather than being silently truncated.
    [InlineData("deploy-stuck-abc", "deploy-stuck-abc")]
    [InlineData("platform-release-skew-0123456789abcdef0123456789abcde", "platform-release-skew-0123456789abcdef0123456789abcde")]
    [InlineData("platform-release-skew-0123456789ABCDEF0123456789ABCDEF", "platform-release-skew-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("platform-release-skew-0123456789abcdef0123456789abcdez", "platform-release-skew-0123456789abcdef0123456789abcdez")]
    public void RuleIsRecoveredFromTheServerFindingIdShape(string findingId, string expectedRule)
        => Assert.Equal(expectedRule, RemediationIntentMap.ExtractRule(findingId));

    // ---- Unmapped and unknown rules refuse ----

    [Theory]
    // Known server rules whose actuators do not exist yet (issue #156 vocabulary expansion).
    [InlineData("serving-latency-slo-breach")]
    [InlineData("gp-queue-depth")]
    [InlineData("alert-dispatch-backlog")]
    [InlineData("alert-dispatch-channel-failure")]
    [InlineData("db-bounded-admission-pressure")]
    [InlineData("pending-contract-migrations")]
    [InlineData("local-backend-substrate-incompatible")]
    // A rule this agent has never heard of.
    [InlineData("some-future-rule")]
    public void UnmappedFindingRuleStaysUnresolved(string rule)
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(
            findingId: $"{rule}-{Digest}",
            actionName: null,
            operationId: "deploy-123");

        Assert.False(intent.IsResolved);
        Assert.Null(intent.Action);
        Assert.Equal(RemediationIntentSource.Unresolved, intent.Source);
        Assert.Contains(rule, intent.Detail, StringComparison.Ordinal);
    }

    // Rules that differ only by prefix must never collapse into each other: the map is an
    // exact lookup on the recovered rule id, not a prefix or contains match.
    [Fact]
    public void RuleLookupIsExactNotPrefixOrSubstring()
    {
        Assert.False(RemediationIntentMap
            .Resolve($"platform-release-skew-extended-{Digest}", null, null)
            .IsResolved);
        Assert.False(RemediationIntentMap
            .Resolve($"pre-platform-release-skew-{Digest}", null, null)
            .IsResolved);
        Assert.Contains("alert-dispatch-backlog", RemediationIntentMap.KnownUnmappedRules);
        Assert.Contains("alert-dispatch-channel-failure", RemediationIntentMap.KnownUnmappedRules);
    }

    // ---- Typed action names ----

    [Theory]
    [InlineData(RemediationAction.GitOpsRollback)]
    [InlineData(RemediationAction.DriftObserve)]
    public void RegisteredActionNameResolvesToItself(string action)
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(null, action, "deploy-123");

        Assert.True(intent.IsResolved);
        Assert.Equal(action, intent.Action);
        Assert.Equal(RemediationIntentSource.TypedAction, intent.Source);
    }

    [Theory]
    [InlineData("restart-service")]
    [InlineData("clear-tile-cache")]
    [InlineData("scale-out")]
    public void UnregisteredActionNameStaysUnresolved(string action)
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(null, action, "deploy-123");

        Assert.False(intent.IsResolved);
        Assert.Equal(RemediationIntentSource.Unresolved, intent.Source);
    }

    // ---- Ambiguity and missing typed intent ----

    [Fact]
    public void FindingIdAndActionNameThatDisagreeAreAmbiguous()
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(
            findingId: $"platform-release-skew-{Digest}",
            actionName: RemediationAction.GitOpsRollback,
            operationId: "deploy-123");

        Assert.False(intent.IsResolved);
        Assert.Contains("Ambiguous", intent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingIdAndActionNameThatAgreeResolveOnce()
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(
            findingId: $"platform-release-skew-{Digest}",
            actionName: RemediationAction.DriftObserve,
            operationId: null);

        Assert.True(intent.IsResolved);
        Assert.Equal(RemediationAction.DriftObserve, intent.Action);
        Assert.Equal(RemediationIntentSource.FindingId, intent.Source);
    }

    [Fact]
    public void NoTypedIntentStaysUnresolved()
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(null, null, null);

        Assert.False(intent.IsResolved);
        Assert.Equal(RemediationIntentSource.Unresolved, intent.Source);
    }

    // Free text is not an input to classification at all: whatever the prose says, only the
    // typed arguments above can select an actuator.
    [Theory]
    [InlineData("manifest drift detected")]
    [InlineData("drift drift drift")]
    [InlineData("rollback the release now")]
    public void FreeTextIsNeverAClassifier(string prose)
    {
        Assert.False(RemediationIntentMap.Resolve(prose, null, null).IsResolved);
        Assert.False(RemediationIntentMap.Resolve(null, prose, null).IsResolved);
    }

    // ---- Backward-compatible operation-id path ----

    [Fact]
    public void ExplicitOperationIdAloneStillRequestsARollback()
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(null, null, "deploy-123");

        Assert.True(intent.IsResolved);
        Assert.Equal(RemediationAction.GitOpsRollback, intent.Action);
        Assert.Equal(RemediationIntentSource.OperationId, intent.Source);
    }

    // A rollback actuates a NAMED durable operation; without that identity the request stays
    // unresolved rather than reaching the actuator with an empty target.
    [Theory]
    [InlineData("deploy-manual-intervention-0123456789abcdef0123456789abcdef", null)]
    [InlineData(null, RemediationAction.GitOpsRollback)]
    public void RollbackWithoutAnOperationIdStaysUnresolved(string? findingId, string? actionName)
    {
        RemediationIntent intent = RemediationIntentMap.Resolve(findingId, actionName, operationId: null);

        Assert.False(intent.IsResolved);
        Assert.Contains("operation id", intent.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // Read-only observation needs no operation id.
    [Fact]
    public void DriftObserveNeedsNoOperationId()
        => Assert.True(RemediationIntentMap.Resolve($"platform-release-skew-{Digest}", null, null).IsResolved);

    // ---- The map only ever names registered actuators ----

    [Fact]
    public void EveryMappedRuleResolvesToARegisteredActuator()
    {
        Assert.NotEmpty(RemediationIntentMap.MappedRules);
        foreach (string rule in RemediationIntentMap.MappedRules)
        {
            RemediationIntent intent = RemediationIntentMap.Resolve($"{rule}-{Digest}", null, "deploy-123");
            Assert.True(intent.IsResolved, $"Rule `{rule}` is mapped but did not resolve.");
            Assert.True(
                ActuatorRegistry.TryResolveRemediation(intent.Action, out _),
                $"Rule `{rule}` maps to `{intent.Action}`, which is not a registered actuator.");
        }

        // A rule is either mapped or explicitly known-unmapped, never both.
        Assert.Empty(RemediationIntentMap.MappedRules.Intersect(RemediationIntentMap.KnownUnmappedRules, StringComparer.OrdinalIgnoreCase));
    }
}
