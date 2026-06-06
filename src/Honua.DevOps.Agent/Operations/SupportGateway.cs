using System.Net.Http.Headers;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations;

internal sealed class SupportGateway : IDisposable
{
    private const string OperatorRoleHeader = "X-Operator-Role";

    // Pinned schema version for the support-context-v1 contract carried on the auto-bundle
    // request body (honua-support docs/contracts/support-context-v1.schema.json). Additive
    // fields keep this at "1.0"; bump only on a breaking change.
    private const string SupportContextSchemaVersion = "1.0";

    private readonly BackendConfiguration configuration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpJsonTransport _transport;

    internal SupportGateway(BackendConfiguration configuration, HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        _httpClient = httpClient ?? HttpClientFactory.Create(configuration.RequestTimeout);
        _ownsHttpClient = httpClient is null;
        _transport = new HttpJsonTransport(_httpClient);
    }

    internal BackendConfiguration Configuration => configuration;

    internal async Task<BackendCallResult> ListPendingTicketsAsync(CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        return await SendAsync(
            HttpMethod.Get,
            configuration.SupportApiTicketsPath,
            payload: null,
            cancellationToken);
    }

    internal async Task<BackendJsonResult> ListPendingTicketsJsonAsync(CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendJsonResult(
                new BackendCallResult(
                    IsSuccess: false,
                    Endpoint: "none",
                    Detail: "support-api-disabled",
                    PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable."),
                null);
        }

        return await SendJsonAsync(
            HttpMethod.Get,
            configuration.SupportApiTicketsPath,
            payload: null,
            cancellationToken);
    }

    internal async Task<BackendCallResult> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        string relativePath = BuildTicketPath(ticketId);
        return await SendAsync(
            HttpMethod.Get,
            relativePath,
            payload: null,
            cancellationToken);
    }

    internal async Task<BackendCallResult> PostDiagnosisAsync(
        string ticketId,
        GuidedFixResult diagnosis,
        OperationEvidence? evidence = null,
        DiagnosisScorecard? diagnosisScorecard = null,
        SupportTicketTrust? trust = null,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        string relativePath = BuildTicketPath(ticketId, "diagnosis");
        object payload = new
        {
            summary = diagnosis.DiagnosisSummary,
            confidence = diagnosis.Confidence,
            mode = diagnosis.Mode.ToConfigValue(),
            guidedCommands = diagnosis.GuidedCommands,
            validationSteps = diagnosis.ValidationSteps,
            evidence,
            diagnosisScorecard,
            escalation = diagnosis.Escalation is null
                ? null
                : new
                {
                    justification = diagnosis.Escalation.Justification,
                    accessScope = diagnosis.Escalation.AccessScope,
                    ttlMinutes = diagnosis.Escalation.TtlMinutes,
                    rollbackIntent = diagnosis.Escalation.RollbackIntent,
                    // Escalation rationale: the signal/trigger that caused the hand-off, so
                    // honua-support and the console can render "why escalated" alongside the
                    // diagnosis without re-deriving it from the justification sentence.
                    trigger = diagnosis.Escalation.Trigger,
                    signal = diagnosis.Escalation.Signal
                },
            // Structured TRUST relay (honua-support#23): the already-computed #70
            // projections (delegated session, scorecard, escalation rationale) travel under
            // a single optional `trust` object so honua-support can persist them verbatim and
            // surface them to the console without re-deriving from prose. Omitted entirely
            // when no projection is supplied so the payload stays backward compatible.
            trust = BuildTrustPayload(trust)
        };

        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            payload,
            cancellationToken);
    }

    // Map the #70 console projections onto the shared TRUST wire contract honua-support
    // consumes verbatim. Every field is optional: any sub-object is omitted when its source
    // projection is absent, and the whole object is null when no trust state was supplied.
    private static object? BuildTrustPayload(SupportTicketTrust? trust)
    {
        if (trust is null ||
            (trust.Session is null && trust.Scorecard is null && trust.Escalation is null))
        {
            return null;
        }

        return new
        {
            delegatedSession = trust.Session is null
                ? null
                : new
                {
                    mode = trust.Session.AccessMode,
                    establishedAt = trust.Session.EstablishedAt,
                    expiresAt = trust.Session.ExpiresAt,
                    customerVisible = trust.Session.CustomerVisible,
                    active = trust.Session.Active
                },
            scorecard = trust.Scorecard is null
                ? null
                : new
                {
                    overallResult = trust.Scorecard.OverallResult,
                    score = trust.Scorecard.CompositeScore,
                    confidence = trust.Scorecard.Confidence,
                    criteria = BuildScorecardCriteria(trust.Scorecard),
                    failureModes = trust.Scorecard.FailureModes,
                    evidenceRefs = trust.Scorecard.Evidence
                        .Select(static reference => reference.RawRef ?? reference.Url ?? reference.Summary)
                        .ToArray()
                },
            escalation = trust.Escalation is null
                ? null
                : new
                {
                    escalated = trust.Escalation.Escalated,
                    trigger = trust.Escalation.Trigger,
                    signal = trust.Escalation.Signal,
                    justification = trust.Escalation.Justification,
                    accessScope = trust.Escalation.AccessScope,
                    ttlMinutes = trust.Escalation.TtlMinutes,
                    rollbackIntent = trust.Escalation.RollbackIntent
                }
        };
    }

    // Flatten the per-criterion booleans the scorecard bridge already computed into the
    // { name, passed } list the wire contract carries, preserving the scorecard's own
    // criterion vocabulary so honua-support renders a checklist without re-deriving it.
    private static IReadOnlyList<object> BuildScorecardCriteria(DiagnosisScorecardBridge scorecard)
        =>
        [
            new { name = "diagnosis-correct", passed = scorecard.DiagnosisCorrect },
            new { name = "remediation-safe", passed = scorecard.RemediationSafe },
            new { name = "policy-compliant", passed = scorecard.PolicyCompliant },
            new { name = "rollback-guidance-correct", passed = scorecard.RollbackGuidanceCorrect },
            new { name = "recovery-verified", passed = scorecard.RecoveryVerified },
            new { name = "service-health-restored", passed = scorecard.ServiceHealthRestored }
        ];

    internal async Task<BackendCallResult> TriggerAutoBundleAsync(
        string ticketId,
        string? instanceUrl = null,
        string? apiKey = null,
        SupportContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        if (!configuration.SupportAutoBundleEnabled)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "auto-bundle-disabled",
                PayloadPreview: "Auto-bundle is disabled. Set HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_ENABLED=true to opt in.");
        }

        if (!TryValidateInstanceHost(instanceUrl, configuration.SupportAutoBundleAllowedHosts, out string hostRejection))
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "auto-bundle-host-rejected",
                PayloadPreview: hostRejection);
        }

        string relativePath = BuildTicketPath(ticketId, "auto-bundle");
        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            BuildAutoBundlePayload(instanceUrl, apiKey, context),
            cancellationToken);
    }

    // Build the auto-bundle request body as the support-context-v1 superset
    // (honua-support docs/contracts/support-context-v1.schema.json). schemaVersion is the
    // single required field; every other property is emitted only when known so honua-support
    // never receives a fabricated value. instanceUrl + apiKey keep the legacy forwarding
    // posture working: instanceUrl falls back to the context's value, and the forwarded key is
    // carried both as the contract's `scopedKey` (read-only telemetry key, treated as a
    // secret) and as the legacy `apiKey` field so an older honua-support stays compatible.
    private static object BuildAutoBundlePayload(string? instanceUrl, string? apiKey, SupportContext? context)
    {
        string? effectiveInstanceUrl = Coalesce(instanceUrl, context?.InstanceUrl);
        string? scopedKey = Coalesce(apiKey, context?.ScopedKey);

        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["schemaVersion"] = SupportContextSchemaVersion
        };

        AddIfPresent(payload, "user", BuildUser(context?.User));
        AddIfPresent(payload, "tenant", BuildTenant(context?.Tenant));
        AddIfPresent(payload, "envKind", Trimmed(context?.EnvKind));
        AddIfPresent(payload, "appVersion", Trimmed(context?.AppVersion));
        AddIfPresent(payload, "commit", Trimmed(context?.Commit));
        AddIfPresent(payload, "route", Trimmed(context?.Route));
        AddIfPresent(payload, "recentErrors", BuildRecentErrors(context?.RecentErrors));
        AddIfPresent(payload, "instanceUrl", Trimmed(effectiveInstanceUrl));
        // scopedKey is the contract's canonical telemetry-key field; apiKey preserves the
        // existing wire field so the request stays backward compatible. Both carry the same
        // forwarded value and are omitted when no key is available.
        AddIfPresent(payload, "scopedKey", Trimmed(scopedKey));
        AddIfPresent(payload, "apiKey", Trimmed(scopedKey));

        return payload;
    }

    private static object? BuildUser(SupportContextUser? user)
    {
        if (user is null)
        {
            return null;
        }

        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        AddIfPresent(result, "id", Trimmed(user.Id));
        AddIfPresent(result, "email", Trimmed(user.Email));
        AddIfPresent(result, "displayName", Trimmed(user.DisplayName));
        return result.Count == 0 ? null : result;
    }

    private static object? BuildTenant(SupportContextTenant? tenant)
    {
        if (tenant is null)
        {
            return null;
        }

        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        AddIfPresent(result, "id", Trimmed(tenant.Id));
        AddIfPresent(result, "name", Trimmed(tenant.Name));
        return result.Count == 0 ? null : result;
    }

    private static object? BuildRecentErrors(IReadOnlyList<SupportContextRecentError>? recentErrors)
    {
        if (recentErrors is null || recentErrors.Count == 0)
        {
            return null;
        }

        List<object> items = [];
        foreach (SupportContextRecentError error in recentErrors)
        {
            Dictionary<string, object?> item = new(StringComparer.Ordinal);
            AddIfPresent(item, "timestamp", Trimmed(error.Timestamp));
            AddIfPresent(item, "message", Trimmed(error.Message));
            AddIfPresent(item, "correlationId", Trimmed(error.CorrelationId));
            AddIfPresent(item, "path", Trimmed(error.Path));
            if (error.StatusCode is int statusCode and >= 100 and <= 599)
            {
                item["statusCode"] = statusCode;
            }

            if (item.Count > 0)
            {
                items.Add(item);
            }
        }

        return items.Count == 0 ? null : items;
    }

    private static void AddIfPresent(Dictionary<string, object?> target, string key, object? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Coalesce(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private static bool TryValidateInstanceHost(
        string? instanceUrl,
        IReadOnlyList<string>? allowedHosts,
        out string rejection)
    {
        rejection = string.Empty;

        if (string.IsNullOrWhiteSpace(instanceUrl))
        {
            rejection = "Auto-bundle requires a non-empty instanceUrl.";
            return false;
        }

        if (!Uri.TryCreate(instanceUrl.Trim(), UriKind.Absolute, out Uri? parsed) ||
            (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            rejection = "Auto-bundle instanceUrl must be an absolute http(s) URL.";
            return false;
        }

        if (allowedHosts is null || allowedHosts.Count == 0)
        {
            rejection =
                "Auto-bundle host allowlist is empty. Set HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_ALLOWED_HOSTS to a comma-separated list of permitted hosts.";
            return false;
        }

        string host = parsed.Host.ToLowerInvariant();
        if (!allowedHosts.Any(allowed => string.Equals(allowed, host, StringComparison.Ordinal)))
        {
            rejection =
                $"Auto-bundle instanceUrl host `{host}` is not in HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_ALLOWED_HOSTS.";
            return false;
        }

        return true;
    }

    internal async Task<BackendCallResult> CloseTicketAsync(
        string ticketId,
        string resolutionSummary,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        string relativePath = BuildTicketPath(ticketId, "close");
        object payload = new { resolutionSummary };
        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            payload,
            cancellationToken);
    }

    private string BuildTicketPath(string ticketId, string? childPath = null)
    {
        string trimmedTicketId = ticketId.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTicketId))
        {
            throw new ArgumentException("Ticket id is required.", nameof(ticketId));
        }

        string encodedTicketId = Uri.EscapeDataString(trimmedTicketId);
        return string.IsNullOrWhiteSpace(childPath)
            ? $"{configuration.SupportApiTicketsPath}/{encodedTicketId}"
            : $"{configuration.SupportApiTicketsPath}/{encodedTicketId}/{childPath}";
    }

    private Task<BackendCallResult> SendAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BackendGateway.BuildEndpoint(configuration.SupportApiBaseUri!, relativePath);
        return _transport.SendAsync(method, endpoint, payload, ApplySupportAuthentication, cancellationToken);
    }

    private Task<BackendJsonResult> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BackendGateway.BuildEndpoint(configuration.SupportApiBaseUri!, relativePath);
        return _transport.SendJsonAsync(method, endpoint, payload, ApplySupportAuthentication, cancellationToken);
    }

    private void ApplySupportAuthentication(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(configuration.SupportApiBearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.SupportApiBearerToken);
            return;
        }

        request.Headers.TryAddWithoutValidation(OperatorRoleHeader, "true");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
