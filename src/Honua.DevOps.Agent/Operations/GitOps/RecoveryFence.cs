using System.Text.Json;
using System.Text.Json.Serialization;

using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Operations.GitOps;

// The recovery fence honua-server enforces on `POST .../operations/{id}/rollback`
// (honua-server#4958, delivered by #4963). A protected activation seals a grant at exposure
// and publishes its terms on `protection`; a rollback that declares any fence term is admitted
// only when every declared term matches, and is otherwise refused at admission with a stable
// `recovery_fence_*` code and no operation transition.
//
// DevOps never invents a term. The target, revision pair, policy digest and expiry come from
// the DeploymentRecoveryGrant sealed at approval (already compared against the observed
// operation); the grant id, actor, tenant and permitted compensation are quoted from the
// server's own sealed record. So the server re-checks the exact scope the approval covered,
// and a grant from a superseded window, another principal or an elapsed expiry is refused by
// the server even if a local check were bypassed.
internal sealed record RecoveryFence(
    string TargetId,
    string ExpectedCandidateRevision,
    string ExpectedPreviousRevision,
    string ExpectedProtectionPhase,
    string GrantId,
    string PolicyDigest,
    string Actor,
    string? TenantId,
    DateTimeOffset NotAfter,
    string Compensation)
{
    internal const string RestorePreviousRevision = "restore-previous-revision";
    internal const string RefusalCodePrefix = "recovery_fence_";

    // Builds the fence for `grant` from the currently observed operation. Null, with a reason,
    // when the server has not sealed a grant this recovery can quote: without a sealed grant
    // nothing on the server would enforce the declared scope.
    internal static RecoveryFence? TryBuild(
        ActuationSpine.DeploymentRecoveryGrant grant,
        JsonElement operation,
        out string refusal)
    {
        ArgumentNullException.ThrowIfNull(grant);

        string? serverCompensation = ServerCompensation(grant.Compensation);
        if (serverCompensation is null)
        {
            refusal = $"Recovery grant for operation `{grant.OperationId}` permits `{grant.Compensation}`, which the server's recovery fence does not implement.";
            return null;
        }

        if (!operation.TryGetProperty("protection", out JsonElement protection) || protection.ValueKind != JsonValueKind.Object)
        {
            refusal = $"Operation `{grant.OperationId}` has no protection window, so the server has sealed no recovery grant to quote.";
            return null;
        }

        string? grantId = ReadString(protection, "grantId");
        string? actor = ReadString(protection, "actor");
        string? phase = ReadString(protection, "phase");
        string? permitted = ReadString(protection, "permittedCompensation");
        if (grantId is null || actor is null || phase is null || permitted is null)
        {
            refusal = $"Operation `{grant.OperationId}` publishes no sealed recovery grant (grant id, actor, phase and permitted compensation are required); " +
                "the server would not enforce this recovery's scope.";
            return null;
        }

        if (!string.Equals(permitted, serverCompensation, StringComparison.Ordinal))
        {
            refusal = $"Operation `{grant.OperationId}` permits compensation `{permitted}`, not the approved `{serverCompensation}`.";
            return null;
        }

        // The server seals a tenant only on a multi-tenant installation. When it does, it must be
        // the tenant this recovery was approved for: quoting another tenant's sealed grant would
        // pass the server's own check for a caller of that tenant while widening the approval.
        string? tenant = ReadString(protection, "tenantId");
        if (tenant is not null && !string.Equals(tenant, grant.Tenant, StringComparison.Ordinal))
        {
            refusal = $"Operation `{grant.OperationId}` sealed its recovery grant for tenant `{tenant}`, not the approved tenant `{grant.Tenant}`.";
            return null;
        }

        refusal = string.Empty;
        return new RecoveryFence(
            TargetId: grant.Target,
            ExpectedCandidateRevision: grant.CandidateRevision,
            ExpectedPreviousRevision: grant.PriorRevision,
            ExpectedProtectionPhase: phase,
            GrantId: grantId,
            PolicyDigest: grant.SafetyPolicyDigest,
            Actor: actor,
            TenantId: tenant,
            NotAfter: grant.ExpiresAtUtc,
            Compensation: serverCompensation);
    }

    internal static string? ServerCompensation(ActuationSpine.PermittedCompensation compensation)
        => compensation == ActuationSpine.PermittedCompensation.RestorePriorRevision ? RestorePreviousRevision : null;

    // The stable refusal code of a fence refusal, or null when the response is not one. A
    // fence refusal is a definite answer: the server admitted nothing and recorded nothing.
    internal static string? ReadRefusalCode(JsonDocument? payload)
        => payload is not null
            && payload.RootElement.ValueKind == JsonValueKind.Object
            && ReadString(payload.RootElement, "code") is { } code
            && code.StartsWith(RefusalCodePrefix, StringComparison.Ordinal)
                ? code
                : null;

    // Wire shape. The server refuses unknown properties, so only fence terms and `reason` are
    // sent; an absent tenant (single-tenant installation) is omitted rather than sent empty.
    internal RequestBody ToRequestBody(string reason)
        => new(reason, TargetId, ExpectedCandidateRevision, ExpectedPreviousRevision, ExpectedProtectionPhase,
            GrantId, PolicyDigest, Actor, TenantId, NotAfter, Compensation);

    internal sealed record RequestBody(
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("targetId")] string TargetId,
        [property: JsonPropertyName("expectedCandidateRevision")] string ExpectedCandidateRevision,
        [property: JsonPropertyName("expectedPreviousRevision")] string ExpectedPreviousRevision,
        [property: JsonPropertyName("expectedProtectionPhase")] string ExpectedProtectionPhase,
        [property: JsonPropertyName("grantId")] string GrantId,
        [property: JsonPropertyName("policyDigest")] string PolicyDigest,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("tenantId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId,
        [property: JsonPropertyName("notAfter")] DateTimeOffset NotAfter,
        [property: JsonPropertyName("compensation")] string Compensation);

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
}
