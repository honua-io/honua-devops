using System.Globalization;
using System.Text.Json;

using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Operations.Observability;

/// <summary>
/// Runs one bounded observe -&gt; diagnose -&gt; propose cycle against the server-owned MCP
/// observability contract. Read evidence comes from MCP; a selected recommended action is handed
/// back by deterministic finding id so Honua alone materializes its hidden payload and routes it
/// through the operation gateway, approval lane, and autonomy evaluator.
/// </summary>
internal sealed class OpsObserveDiagnoseProposeLoop(
    OperationRuntime runtime,
    BackendGateway gateway,
    ActuationSpine? spine = null)
{
    private readonly ActuationSpine _spine = spine ?? new ActuationSpine(runtime, OperatorPolicy.OperatorPolicy.Default);

    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 50;
    private const int DefaultLookbackHours = 24;
    private const int MaxLookbackHours = 168;
    private const int MaxFindings = 50;
    private const int MaxEvidenceRefsPerFinding = 12;
    private const int MaxTextCharacters = 2048;
    private const int MaxReferenceCharacters = 512;

    internal async Task<OpsLoopReport> RunAsync(
        string findingId,
        string severity,
        string rule,
        int lookbackHours,
        int pageSize,
        bool proposeRecommendedAction,
        CancellationToken cancellationToken = default)
    {
        int boundedPageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        int boundedLookbackHours = Math.Clamp(
            lookbackHours <= 0 ? DefaultLookbackHours : lookbackHours,
            1,
            MaxLookbackHours);
        string? normalizedFindingId = NormalizeOptional(findingId, 256, nameof(findingId));
        string? normalizedSeverity = NormalizeSeverity(severity);
        string? normalizedRule = NormalizeOptional(rule, 128, nameof(rule));
        DateTimeOffset to = DateTimeOffset.UtcNow;
        DateTimeOffset from = to.AddHours(-boundedLookbackHours);
        List<string> limitations = [];
        List<string> toolsUsed = [];

        await using HonuaMcpOpsClient client = gateway.CreateMcpOpsClient();
        JsonElement health;
        JsonElement findingsPayload;
        JsonElement alertsPayload;
        JsonElement eventsPayload;
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

            health = await CallRequiredAsync(client, "honua_ops_health", new { }, toolsUsed, cancellationToken)
                .ConfigureAwait(false);

            Dictionary<string, object> findingArguments = [];
            AddIfPresent(findingArguments, "findingId", normalizedFindingId);
            AddIfPresent(findingArguments, "severity", normalizedSeverity);
            AddIfPresent(findingArguments, "rule", normalizedRule);
            findingsPayload = await CallRequiredAsync(
                    client,
                    "honua_ops_findings",
                    findingArguments,
                    toolsUsed,
                    cancellationToken)
                .ConfigureAwait(false);

            Dictionary<string, object> alertArguments = new()
            {
                ["from"] = from,
                ["to"] = to,
                ["pageSize"] = boundedPageSize
            };
            alertsPayload = await CallRequiredAsync(
                    client,
                    "honua_alert_events",
                    alertArguments,
                    toolsUsed,
                    cancellationToken)
                .ConfigureAwait(false);

            eventsPayload = await CallRequiredAsync(
                    client,
                    "honua_operate_events",
                    new
                    {
                        from,
                        to,
                        pageSize = boundedPageSize
                    },
                    toolsUsed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HonuaMcpContractException exception)
        {
            limitations.Add($"Required MCP read failed closed at `{exception.Operation}`: {Redaction.Scrub(exception.Message)}");
            return EmptyUnavailableReport(boundedPageSize, boundedLookbackHours, toolsUsed, limitations);
        }

        JsonElement? platformRelease = await TryCallOptionalAsync(
                client,
                "honua_platform_release_status",
                new { },
                toolsUsed,
                limitations,
                cancellationToken)
            .ConfigureAwait(false);

        JsonElement? deployOperationsPayload = await TryCallOptionalAsync(
                client,
                "honua_deploy_operations",
                new { page = 1, pageSize = boundedPageSize },
                toolsUsed,
                limitations,
                cancellationToken)
            .ConfigureAwait(false);

        SupportedKindsResult supportedKinds;
        if (proposeRecommendedAction && runtime.ExecutionTier >= ExecutionTier.Propose)
        {
            supportedKinds = await DiscoverSupportedKindsAsync(
                    client,
                    toolsUsed,
                    limitations,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            supportedKinds = new SupportedKindsResult(false, []);
            limitations.Add(proposeRecommendedAction
                ? "Executor discovery was skipped because the local tier cannot propose; the read phase called read-only MCP tools only."
                : "Executor discovery was skipped because no proposal was requested; the read phase called read-only MCP tools only.");
        }

        string overallHealth;
        IReadOnlyList<OpsLoopAlertEvidence> alerts;
        IReadOnlyList<OpsLoopEventEvidence> events;
        try
        {
            overallHealth = ReadRequiredString(health, "overallStatus", "honua_ops_health");
            alerts = ParseAlerts(alertsPayload, boundedPageSize);
            events = ParseEvents(eventsPayload, boundedPageSize);
        }
        catch (HonuaMcpContractException exception)
        {
            limitations.Add($"Required MCP payload failed closed at `{exception.Operation}`: {Redaction.Scrub(exception.Message)}");
            return EmptyUnavailableReport(boundedPageSize, boundedLookbackHours, toolsUsed, limitations);
        }

        if (ReadOptionalString(alertsPayload, "nextCursor") is not null)
        {
            limitations.Add("Alert history has another page; this invocation retained only the bounded first page.");
        }

        if (ReadOptionalBoolean(eventsPayload, "partialResult") == true)
        {
            limitations.Add("The server reported a partial Operate timeline result; source diagnostics remain server-owned.");
        }

        IReadOnlyList<OpsLoopDeployEvidence> deployOperations = [];
        if (deployOperationsPayload is JsonElement deployPayload)
        {
            try
            {
                deployOperations = ParseDeployOperations(deployPayload, boundedPageSize);
                if (ReadOptionalBoolean(deployPayload, "hasMore") == true)
                {
                    limitations.Add("Deploy history has another page; this invocation retained only the bounded first page.");
                }
            }
            catch (HonuaMcpContractException exception)
            {
                limitations.Add($"Optional deploy correlation was malformed: {Redaction.Scrub(exception.Message)}");
            }
        }

        FindingParseResult parsedFindings;
        try
        {
            parsedFindings = ParseFindings(
                findingsPayload,
                supportedKinds.Verified,
                supportedKinds.Kinds,
                alerts,
                events,
                deployOperations);
        }
        catch (HonuaMcpContractException exception)
        {
            limitations.Add($"Required MCP payload failed closed at `{exception.Operation}`: {Redaction.Scrub(exception.Message)}");
            return EmptyUnavailableReport(boundedPageSize, boundedLookbackHours, toolsUsed, limitations);
        }

        List<OpsLoopFindingReport> findings = parsedFindings.Findings.ToList();

        string status = "diagnosed";
        if (proposeRecommendedAction)
        {
            status = await TryProposeOneAsync(
                    findings,
                    supportedKinds,
                    limitations,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (parsedFindings.Truncated)
        {
            limitations.Add($"Finding output was capped at {MaxFindings} entries.");
        }

        string? releaseVersion = platformRelease is JsonElement release
            ? ReadOptionalString(release, "releaseVersion")
            : null;
        bool? coVersioned = platformRelease is JsonElement releaseStatus
            ? ReadOptionalBoolean(releaseStatus, "isCoVersioned")
            : null;
        IReadOnlyList<string> skewedIds = platformRelease is JsonElement releaseSkew
            ? ReadStringArray(releaseSkew, "skewedIds", MaxPageSize)
            : [];

        return new OpsLoopReport(
            Status: status,
            ObservabilitySource: "honua-server-mcp",
            OverallHealth: overallHealth,
            PlatformReleaseVersion: releaseVersion,
            PlatformReleaseCoVersioned: coVersioned,
            PlatformReleaseSkewedIds: skewedIds,
            SupportedKindsVerified: supportedKinds.Verified,
            SupportedKinds: supportedKinds.Kinds,
            Findings: findings,
            AlertHistory: alerts,
            OperateTimeline: events,
            DeployOperations: deployOperations,
            McpToolsUsed: toolsUsed.Distinct(StringComparer.Ordinal).ToArray(),
            Bounds: new OpsLoopBounds(
                boundedPageSize,
                boundedLookbackHours,
                MaxFindings,
                MaxEvidenceRefsPerFinding,
                MaxTextCharacters,
                parsedFindings.Truncated),
            Limitations: limitations);
    }

    private async Task<string> TryProposeOneAsync(
        List<OpsLoopFindingReport> findings,
        SupportedKindsResult supportedKinds,
        List<string> limitations,
        CancellationToken cancellationToken)
    {
        if (runtime.ExecutionTier < ExecutionTier.Propose)
        {
            limitations.Add("Proposal creation requires execution tier `propose` or higher; this run remained read-only.");
            return "proposal-not-authorized";
        }

        if (!supportedKinds.Verified)
        {
            limitations.Add("The live executor catalog could not be verified; proposal creation failed closed.");
            return "proposal-support-unverified";
        }

        OpsLoopFindingReport? candidate = findings.FirstOrDefault(finding => finding.RecommendedAction?.Supported == true);
        if (candidate is null)
        {
            bool hasRecommendation = findings.Any(finding => finding.RecommendedAction is not null);
            return hasRecommendation ? "no-supported-action" : "no-actionable-finding";
        }

        int additionalCandidates = findings.Count(finding => finding.RecommendedAction?.Supported == true) - 1;
        if (additionalCandidates > 0)
        {
            limitations.Add(
                $"This bounded invocation proposed one finding only; {additionalCandidates} additional supported candidate(s) remain for explicit review.");
        }

        // Creating a server-owned proposal is a lifecycle-entry write: it records a governed
        // request and executes nothing. It goes through the actuation spine so the finding
        // identity and the policy decision are sealed with it, and so it fails closed when
        // the audit/receipt sink is unavailable (issue #153).
        ActuationAuthorization authorization = _spine.Authorize(new ActuationRequest(
            ActuatorId: "honua.ops-finding.propose",
            Action: "propose",
            Target: candidate.FindingId,
            Environments: [],
            DesiredState: $"finding={candidate.FindingId}",
            IdempotencyKey: $"honua-devops:ops-finding:{candidate.FindingId}",
            PolicyGate: "proposal-required",
            AuthorizationDryRun: true,
            Actor: "honua-devops:ops-loop",
            LifecycleEntry: BackendMutation.OpsFindingPropose));

        if (!authorization.IsGranted)
        {
            limitations.Add($"Finding proposal failed closed: {authorization.Reason}");
            return "proposal-failed";
        }

        using BackendJsonResult result = await gateway
            .ProposeOpsFindingAsync(candidate.FindingId, authorization.Grant!, cancellationToken)
            .ConfigureAwait(false);
        if (!result.CallResult.IsSuccess || result.Payload is null)
        {
            limitations.Add(
                $"Finding proposal failed closed: {result.CallResult.Detail}. The server gateway did not confirm an outcome.");
            return "proposal-failed";
        }

        OpsLoopProposal proposal = ParseProposal(candidate.FindingId, result.Payload.RootElement);
        int index = findings.FindIndex(finding => string.Equals(
            finding.FindingId,
            candidate.FindingId,
            StringComparison.Ordinal));
        findings[index] = candidate with { Proposal = proposal };

        return proposal.GatewayStatus switch
        {
            "ProposalCreated" => "proposal-created",
            "Executed" => "executed-by-server-policy",
            "Failed" => "execution-failed",
            "RolledBack" => "execution-rolled-back",
            "Indeterminate" => "execution-indeterminate",
            "Canceled" => "execution-canceled",
            "Blocked" or "NotSupported" => "proposal-blocked",
            _ => "proposal-routed"
        };
    }

    private static async Task<JsonElement> CallRequiredAsync(
        HonuaMcpOpsClient client,
        string toolName,
        object arguments,
        List<string> toolsUsed,
        CancellationToken cancellationToken)
    {
        toolsUsed.Add(toolName);
        return await client.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement?> TryCallOptionalAsync(
        HonuaMcpOpsClient client,
        string toolName,
        object arguments,
        List<string> toolsUsed,
        List<string> limitations,
        CancellationToken cancellationToken)
    {
        toolsUsed.Add(toolName);
        try
        {
            return await client.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (HonuaMcpContractException exception)
        {
            limitations.Add($"Optional MCP correlation `{toolName}` was unavailable: {Redaction.Scrub(exception.Message)}");
            return null;
        }
    }

    private static async Task<SupportedKindsResult> DiscoverSupportedKindsAsync(
        HonuaMcpOpsClient client,
        List<string> toolsUsed,
        List<string> limitations,
        CancellationToken cancellationToken)
    {
        const string toolName = "honua_propose_operation";
        toolsUsed.Add(toolName);
        try
        {
            // The server intentionally reports supportedKinds on every response, including its
            // missing-kind rejection. Empty arguments therefore discover the live executor catalog
            // without constructing or routing an operation.
            JsonElement result = await client.CallToolAsync(toolName, new { }, cancellationToken)
                .ConfigureAwait(false);
            string? outcome = ReadOptionalString(result, "outcome");
            string? proposalId = ReadOptionalString(result, "proposalId");
            string? executionOperationId = ReadOptionalString(result, "executionOperationId");
            IReadOnlyList<string> kinds = ReadStringArray(result, "supportedKinds", MaxPageSize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToArray();

            bool verified = string.Equals(outcome, "rejected", StringComparison.OrdinalIgnoreCase) &&
                proposalId is null &&
                executionOperationId is null &&
                kinds.Count > 0;
            if (!verified)
            {
                limitations.Add("Executor discovery did not return the expected non-routing rejection; proposals fail closed.");
            }

            return new SupportedKindsResult(verified, verified ? kinds : []);
        }
        catch (HonuaMcpContractException exception)
        {
            limitations.Add(
                $"Executor discovery through `{toolName}` was unavailable: {Redaction.Scrub(exception.Message)}");
            return new SupportedKindsResult(false, []);
        }
    }

    private static FindingParseResult ParseFindings(
        JsonElement payload,
        bool supportedKindsVerified,
        IReadOnlyList<string> supportedKinds,
        IReadOnlyList<OpsLoopAlertEvidence> alerts,
        IReadOnlyList<OpsLoopEventEvidence> events,
        IReadOnlyList<OpsLoopDeployEvidence> deployOperations)
    {
        if (!payload.TryGetProperty("findings", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new HonuaMcpContractException("honua_ops_findings", "The response omitted the findings array.");
        }

        HashSet<string> supported = new(supportedKinds, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, OpsLoopFindingReport> unique = new(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string findingId = ReadRequiredString(item, "id", "honua_ops_findings");
            if (unique.ContainsKey(findingId))
            {
                continue;
            }

            string? targetId = ReadNestedOptionalString(item, "subject", "targetId");
            string? operationId = ReadNestedOptionalString(item, "subject", "operationId");
            string? releaseVersion = ReadNestedOptionalString(item, "subject", "releaseVersion");
            IReadOnlyList<string> evidenceRefs = ReadStringArray(item, "evidenceRefs", MaxEvidenceRefsPerFinding);
            OpsLoopRecommendedAction? action = ParseRecommendedAction(
                item,
                supportedKindsVerified,
                supported);

            string[] relatedAlerts = alerts
                .Where(alert => EvidenceMatches(evidenceRefs, alert.EventId, alert.ResourceRef))
                .Select(alert => alert.EventId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] relatedEvents = events
                .Where(@event =>
                    EvidenceMatches(evidenceRefs, @event.EventId, @event.ResourceRef) ||
                    Matches(operationId, @event.OperationId) ||
                    Matches(releaseVersion, @event.ReleaseId))
                .Select(@event => @event.EventId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] relatedDeploys = deployOperations
                .Where(operation =>
                    Matches(operationId, operation.OperationId) ||
                    Matches(targetId, operation.TargetId))
                .Select(operation => operation.OperationId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            unique.Add(
                findingId,
                new OpsLoopFindingReport(
                    FindingId: findingId,
                    Rule: ReadRequiredString(item, "rule", "honua_ops_findings"),
                    Severity: ReadRequiredString(item, "severity", "honua_ops_findings"),
                    Title: ReadRequiredString(item, "title", "honua_ops_findings"),
                    Explanation: ReadRequiredString(item, "explanation", "honua_ops_findings"),
                    DetectedAt: ReadRequiredString(item, "detectedAt", "honua_ops_findings"),
                    TargetId: targetId,
                    OperationId: operationId,
                    ReleaseVersion: releaseVersion,
                    EvidenceRefs: evidenceRefs,
                    RecommendedAction: action,
                    RelatedAlertIds: relatedAlerts,
                    RelatedEventIds: relatedEvents,
                    RelatedDeployOperationIds: relatedDeploys));
        }

        bool truncated = unique.Count > MaxFindings;
        return new FindingParseResult(unique.Values.Take(MaxFindings).ToArray(), truncated);
    }

    private static OpsLoopRecommendedAction? ParseRecommendedAction(
        JsonElement finding,
        bool supportedKindsVerified,
        IReadOnlySet<string> supportedKinds)
    {
        if (!finding.TryGetProperty("recommendedAction", out JsonElement action) ||
            action.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        string kind = ReadRequiredString(action, "kind", "honua_ops_findings.recommendedAction");
        return new OpsLoopRecommendedAction(
            Kind: kind,
            Summary: ReadRequiredString(action, "summary", "honua_ops_findings.recommendedAction"),
            Reason: ReadRequiredString(action, "reason", "honua_ops_findings.recommendedAction"),
            AutoSafe: ReadOptionalBoolean(action, "autoSafe") ?? false,
            BlastRadius: ReadOptionalInteger(action, "blastRadius") ?? 1,
            Supported: supportedKindsVerified && supportedKinds.Contains(kind));
    }

    private static IReadOnlyList<OpsLoopAlertEvidence> ParseAlerts(JsonElement payload, int limit)
    {
        if (!payload.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new HonuaMcpContractException("honua_alert_events", "The response omitted the items array.");
        }

        return items.EnumerateArray()
            .Take(limit)
            .Select(item =>
            {
                string rawId = ReadScalarString(item, "eventId", "honua_alert_events");
                return new OpsLoopAlertEvidence(
                    EventId: rawId.StartsWith("alert:", StringComparison.Ordinal) ? rawId : $"alert:{rawId}",
                    Severity: ReadRequiredString(item, "severity", "honua_alert_events"),
                    OccurredAt: ReadRequiredString(item, "occurredAt", "honua_alert_events"),
                    LifecycleStatus: ReadRequiredString(item, "lifecycleStatus", "honua_alert_events"),
                    RuleName: ReadOptionalString(item, "ruleName"),
                    ResourceRef: ReadOptionalString(item, "resourceRef"));
            })
            .ToArray();
    }

    private static IReadOnlyList<OpsLoopEventEvidence> ParseEvents(JsonElement payload, int limit)
    {
        if (!payload.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new HonuaMcpContractException("honua_operate_events", "The response omitted the items array.");
        }

        return items.EnumerateArray()
            .Take(limit)
            .Select(item => new OpsLoopEventEvidence(
                EventId: ReadRequiredString(item, "eventId", "honua_operate_events"),
                Kind: ReadRequiredString(item, "kind", "honua_operate_events"),
                Severity: ReadRequiredString(item, "severity", "honua_operate_events"),
                OccurredAt: ReadRequiredString(item, "occurredAt", "honua_operate_events"),
                Title: ReadRequiredString(item, "title", "honua_operate_events"),
                Summary: ReadOptionalString(item, "summary"),
                OperationId: ReadOptionalString(item, "operationId"),
                ReleaseId: ReadOptionalString(item, "releaseId"),
                ResourceRef: ReadOptionalString(item, "resourceRef")))
            .ToArray();
    }

    private static IReadOnlyList<OpsLoopDeployEvidence> ParseDeployOperations(JsonElement payload, int limit)
    {
        if (!payload.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new HonuaMcpContractException("honua_deploy_operations", "The response omitted the items array.");
        }

        return items.EnumerateArray()
            .Take(limit)
            .Select(item => new OpsLoopDeployEvidence(
                OperationId: ReadRequiredString(item, "operationId", "honua_deploy_operations"),
                Kind: ReadRequiredString(item, "kind", "honua_deploy_operations"),
                Status: ReadRequiredString(item, "status", "honua_deploy_operations"),
                TargetId: ReadNestedOptionalString(item, "target", "targetId"),
                Environment: ReadNestedOptionalString(item, "target", "environment"),
                CurrentRevision: ReadNestedOptionalString(item, "target", "currentRevision"),
                DesiredRevision: ReadNestedOptionalString(item, "target", "desiredRevision"),
                CurrentPhase: ReadOptionalString(item, "currentPhase"),
                UpdatedAt: ReadRequiredString(item, "updatedAt", "honua_deploy_operations")))
            .ToArray();
    }

    private static OpsLoopProposal ParseProposal(string findingId, JsonElement payload) => new(
        FindingId: ReadOptionalString(payload, "findingId") ?? findingId,
        GatewayStatus: ReadRequiredString(payload, "status", "findings/{id}/propose"),
        ProposalId: ReadOptionalString(payload, "proposalId"),
        ExecutionOperationId: ReadOptionalString(payload, "executionOperationId"),
        Message: ReadOptionalString(payload, "message"));

    private static bool EvidenceMatches(
        IReadOnlyList<string> evidenceRefs,
        string eventId,
        string? resourceRef)
    {
        string normalizedEventId = NormalizeReference(eventId);
        string? normalizedResource = resourceRef is null ? null : NormalizeReference(resourceRef);
        return evidenceRefs.Any(reference =>
        {
            string normalized = NormalizeReference(reference);
            return string.Equals(normalized, normalizedEventId, StringComparison.OrdinalIgnoreCase) ||
                (normalizedResource is not null &&
                    string.Equals(normalized, normalizedResource, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string NormalizeReference(string value) =>
        value.Trim().Replace('/', ':').ToLowerInvariant();

    private static bool Matches(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal);

    private static string ReadRequiredString(JsonElement element, string propertyName, string operation)
    {
        string? value = ReadOptionalString(element, propertyName);
        return value ?? throw new HonuaMcpContractException(
            operation,
            $"The response omitted required string `{propertyName}`.");
    }

    private static string ReadScalarString(JsonElement element, string propertyName, string operation)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new HonuaMcpContractException(operation, $"The response omitted `{propertyName}`.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => ClampText(value.GetString()!, MaxTextCharacters),
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new HonuaMcpContractException(
                operation,
                $"The response field `{propertyName}` was neither a string nor number.")
        };
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? ClampText(value.GetString()!, MaxTextCharacters)
            : null;
    }

    private static string? ReadNestedOptionalString(JsonElement element, string objectName, string propertyName)
    {
        return element.TryGetProperty(objectName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadOptionalString(nested, propertyName)
            : null;
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? ReadOptionalInteger(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName, int limit)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(value => ClampText(value.GetString()!, MaxReferenceCharacters))
            .Take(limit)
            .ToArray();
    }

    private static void AddIfPresent(Dictionary<string, object> arguments, string name, string? value)
    {
        if (value is not null)
        {
            arguments.Add(name, value);
        }
    }

    private static string? NormalizeOptional(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"{parameterName} must be at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string ClampText(string value, int maxLength)
    {
        string normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? NormalizeSeverity(string severity)
    {
        string? normalized = NormalizeOptional(severity, 16, nameof(severity));
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            return "Info";
        }

        if (normalized.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning";
        }

        if (normalized.Equals("critical", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        throw new ArgumentException("severity must be empty, Info, Warning, or Critical.", nameof(severity));
    }

    private static OpsLoopReport EmptyUnavailableReport(
        int pageSize,
        int lookbackHours,
        IReadOnlyList<string> toolsUsed,
        IReadOnlyList<string> limitations) => new(
            Status: "observability-unavailable",
            ObservabilitySource: "honua-server-mcp",
            OverallHealth: null,
            PlatformReleaseVersion: null,
            PlatformReleaseCoVersioned: null,
            PlatformReleaseSkewedIds: [],
            SupportedKindsVerified: false,
            SupportedKinds: [],
            Findings: [],
            AlertHistory: [],
            OperateTimeline: [],
            DeployOperations: [],
            McpToolsUsed: toolsUsed.Distinct(StringComparer.Ordinal).ToArray(),
            Bounds: new OpsLoopBounds(
                pageSize,
                lookbackHours,
                MaxFindings,
                MaxEvidenceRefsPerFinding,
                MaxTextCharacters,
                false),
            Limitations: limitations);

    private sealed record SupportedKindsResult(bool Verified, IReadOnlyList<string> Kinds);

    private sealed record FindingParseResult(IReadOnlyList<OpsLoopFindingReport> Findings, bool Truncated);
}
