using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    // This record is conversational progress, never execution authority. Every write
    // still goes through the existing provisioning/handoff handlers on every call.
    private sealed record InstallationSession(
        string Scope, string Requirements, string Stack, string VariablesJson,
        string OutputDirectory, string Stage = "intake", OperationResponse? Plan = null);

    public async Task<OperationResponse> InstallHonuaAsync(
        string installationId = "", string customerRequirements = "", string stack = "",
        string variablesJson = "", string outputDirectory = "", bool confirmed = false,
        string confirmation = "", string approvalReceiptJson = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installationId)) installationId = Guid.NewGuid().ToString("n");
        if (installationId.Length != 32 || installationId.Any(c => !char.IsAsciiHexDigit(c)))
            return ProvisioningRefusal("installation-invalid", "Use the installationId returned by install_honua.", [], []);

        string key = "installation:" + installationId;
        using FileStream sessionLock = await AcquireLineageLockAsync(key, cancellationToken);
        string path = GetProvisioningStatePath(key);
        string scope = JsonSerializer.Serialize(new { runtime.TerraformLocalPath, runtime.RuntimeKind, runtime.DeployTargetId });
        InstallationSession session;
        try
        {
            session = File.Exists(path)
                ? JsonSerializer.Deserialize<InstallationSession>(await File.ReadAllTextAsync(path, cancellationToken))!
                : new(scope, "", "", "{}", Path.Combine(GetProvisioningStateRoot(), "installs", installationId));
            if (session is null || session.Scope != scope)
                return ProvisioningRefusal("installation-scope-mismatch", "This installation belongs to another host-authorized target.", [], []);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return ProvisioningRefusal("installation-state-invalid", "Installation progress is unreadable; reconcile retained provisioning evidence before retrying.", [], []);
        }

        OperationResponse Progress(OperationResponse response, string stage, string nextStep, string? question = null)
            => response with
            {
                AuditEventId = response.Status == "infrastructure-provisioned"
                    ? response.AuditEventId : Guid.NewGuid().ToString("n"),
                Installation = new(installationId, stage, question, nextStep),
                Actions = [.. response.Actions, nextStep]
            };
        Task SaveAsync() => WriteEvidenceCopyAsync(path, JsonSerializer.SerializeToUtf8Bytes(session), cancellationToken);
        OperationResponse Question(string question) => Progress(
            new("install-input-required", "Installation needs customer input.", [], [], [], []),
            "intake", "Ask the user the next question, then call install_honua with this installationId and their answer.", question);

        if (session.Stage != "intake" &&
            ((!string.IsNullOrWhiteSpace(customerRequirements) && Redaction.Scrub(customerRequirements) != session.Requirements)
            || (!string.IsNullOrWhiteSpace(stack) && NormalizeToken(stack) != session.Stack)
            || (!string.IsNullOrWhiteSpace(variablesJson) && variablesJson != session.VariablesJson)
            || (!string.IsNullOrWhiteSpace(outputDirectory) && Path.GetFullPath(outputDirectory) != session.OutputDirectory)))
            return Progress(ProvisioningRefusal("installation-input-conflict", "The reviewed installation inputs are immutable. Start a new installation to change them.", [], []),
                session.Stage, "Resume with only installationId and the requested confirmation/approval fields.");

        if (session.Stage == "intake")
        {
            session = session with
            {
                Requirements = string.IsNullOrWhiteSpace(customerRequirements) ? session.Requirements : Redaction.Scrub(customerRequirements),
                Stack = string.IsNullOrWhiteSpace(stack) ? session.Stack : NormalizeToken(stack),
                OutputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? session.OutputDirectory : Path.GetFullPath(outputDirectory)
            };
            if (session.Stack.Length != 0)
            {
                if (!ProvisioningStackCatalog.TryResolve(session.Stack, out var target))
                    return Progress(ProvisioningRefusal("install-target-unqualified", "This first-install entry supports aws-ecs/small. For local Docker, follow the published Docker quickstart.", [], []),
                        "blocked", "Choose a qualified first-install target; do not create resources.");
                if (target!.FirstInstallBlockReason is { } reason)
                    return Progress(ProvisioningRefusal("install-target-unqualified", reason, [], []),
                        "blocked", "Use a qualified workflow before attempting this first-install target; do not create resources.");
            }
            if (!string.IsNullOrWhiteSpace(variablesJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(variablesJson);
                    ServerOperationLineage.RejectDuplicateFields(document.RootElement);
                    var merged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(session.VariablesJson)!;
                    foreach (var property in document.RootElement.EnumerateObject()) merged[property.Name] = property.Value.Clone();
                    string mergedJson = JsonSerializer.Serialize(merged);
                    // Reuse the provisioning allowlist before persisting any values.
                    _ = ParseProvisioningVariables(session.Stack.Length == 0 ? "aws-ecs" : session.Stack, mergedJson);
                    session = session with { VariablesJson = mergedJson };
                }
                catch (Exception exception) when (exception is InvalidOperationException or JsonException or InvalidDataException)
                {
                    return Progress(ProvisioningRefusal("variables-invalid", "Provide only the existing provisioning handler's allowlisted non-secret variables; credentials stay in the operator process environment.", [], []),
                        "intake", "Correct variablesJson; never send passwords, IAM keys or signing material.");
                }
            }
            await SaveAsync();
            if (session.Requirements.Length == 0)
                return Question("What will Honua serve, and what are your scale, availability, compliance and budget requirements? Do not include credentials.");
            if (session.Stack.Length == 0)
                return Question("Which install target do you want? aws-ecs/small is qualified for this entry; Lambda and EKS first installs remain unqualified.");
            using var inputs = JsonDocument.Parse(session.VariablesJson);
            foreach (string required in new[] { "environment", "region", "honua_image" })
                if (!inputs.RootElement.TryGetProperty(required, out _))
                    return Question($"What {required} should the install use? Supply it in variablesJson (non-secret values only); previous intake values are retained.");

            string environment = inputs.RootElement.GetProperty("environment").GetString()!;
            ProvisioningTopology topology = ProvisioningTopology.Ecs(ParseAwsEcsSmallVariables(session.VariablesJson));
            List<OperationResponse> intake = [await DescribeEnvironmentAsync(cancellationToken)];
            // An empty target is expected before a first install. Discovery failures
            // are shown as evidence; they cannot grant provisioning authority.
            intake.Add(await AnalyzeCustomerRequirementsAsync(session.Requirements, "small", "customer requirements", "customer requirements", "aws", cancellationToken, enableRedis: topology.Redis, customDomain: topology.CustomDomain));
            intake.Add(await RecommendDeploymentTopologyAsync(environment, false, false, true, session.Requirements, "customer requirements", cancellationToken, enableRedis: topology.Redis, customDomain: topology.CustomDomain));
            OperationResponse plan = await ProvisionInfrastructureAsync(session.Stack, "small", "plan", session.VariablesJson,
                false, "", cancellationToken: cancellationToken, idempotencyKey: key);
            if (plan.Status != "terraform-plan-ready")
                return Progress(plan, "intake", "Resolve the provisioning refusal; a spent/indeterminate plan requires a new installation and fresh approval.");
            plan = plan with
            {
                Findings = [.. intake.SelectMany(r => new[] { r.Summary }.Concat(r.Findings)), .. plan.Findings],
                Risks = [.. plan.Risks, .. intake.Where(r => r.Status is "backend-error" or "environment-degraded")
                    .Select(r => $"Pre-install advisory unavailable ({r.Status}): {r.Summary} No installed-server health or custom topology is established; only the existing small provisioning lane is planned.")],
                BackendSteps = [.. intake.SelectMany(r => r.BackendSteps ?? []), .. plan.BackendSteps ?? []]
            };
            session = session with { Stage = "approval", Plan = plan };
            await SaveAsync();
            return Review(plan);
        }

        OperationResponse Review(OperationResponse plan) => Progress(plan with
        {
            Findings = [.. plan.Findings,
                $"Install region: {JsonDocumentRegion(session.VariablesJson)}.",
                "Cost drivers: ECS Fargate task CPU/memory and replica count, RDS instance/storage/backups, ALB, NAT/network egress, optional Redis and logs. No price quote is implied; review the exact resource changes above.",
                "Use short-lived SSO/OIDC/assumed-role credentials only. This workflow never creates IAM users or long-lived access keys."]
        }, "approval", "Present account/execution identity, topology, exact deploy identity action list, region, resources, cost drivers and planMetadataDigest to the user. Obtain the separate approver's signed receipt for this exact plan, then call install_honua with installationId, confirmed=true, the plan's confirmation challenge and approvalReceiptJson.");

        if (session.Stage == "approval")
        {
            if (!confirmed && string.IsNullOrWhiteSpace(approvalReceiptJson)) return Review(session.Plan!);
            // Correlate the continuation to THIS installation. The handler still
            // owns all plan/receipt validation; a different valid plan is not ours.
            if (session.Plan!.ConfirmationChallenge is null || confirmation != session.Plan.ConfirmationChallenge)
                return Progress(ProvisioningRefusal("installation-plan-mismatch", "Confirmation must identify the exact plan retained and reviewed for this installation.", [], []),
                    "approval", "Use this installation's retained confirmationChallenge and separately signed approval receipt. Do not substitute another installation's plan.");
            OperationResponse apply = await ProvisionInfrastructureAsync(session.Stack, "small", "apply", session.VariablesJson,
                confirmed, confirmation, approvalReceiptJson, cancellationToken);
            if (apply.Status == "infrastructure-provisioned")
            {
                session = session with { Stage = "handoff" };
                await SaveAsync();
            }
            return Progress(apply, session.Stage, apply.Status == "infrastructure-provisioned"
                ? "Call install_honua with installationId to write the secretless handoff. Installation is not verified yet."
                : "Resolve the existing provisioning gate; never retry a spent plan. No handoff is authorized by this result.");
        }

        string operationId = session.Plan!.ProvisioningLineage!.ProvisioningOperationId;
        if (session.Stage == "handoff")
        {
            OperationResponse handoff = await InstallHandoffAsync(session.Stack, "", "", session.OutputDirectory, false, operationId, cancellationToken);
            if (handoff.Status == "install-handoff-written")
            {
                session = session with { Stage = "verify" };
                await SaveAsync();
            }
            return Progress(handoff, session.Stage, "Call install_honua with installationId after resolving any refusal to verify the exact handoff. Installation is not verified yet.");
        }

        OperationResponse verified = await VerifyInstallHandoffAsync(Path.Combine(session.OutputDirectory, "honua-mcp-proxy.handoff.json"), false, cancellationToken);
        if (verified.Status != "install-handoff-verified")
            return Progress(verified, "verify", "Resolve verification failures and resume this installation. Do not claim readiness.");
        session = session with { Stage = "complete" };
        await SaveAsync();
        using var handoffDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(session.OutputDirectory, "honua-mcp-proxy.handoff.json"), cancellationToken));
        string endpoint = handoffDocument.RootElement.GetProperty("env").GetProperty("HONUA_MCP_REMOTE_URL").GetString()!;
        return Progress(verified, "complete", $"Use the verified installed server at {endpoint} for day-2 operations (honua_ops_health, honua_ops_findings and honua_propose_*). Server proposals require a separate principal's REST approval.");
    }

    private static string JsonDocumentRegion(string variablesJson)
    {
        using var document = JsonDocument.Parse(variablesJson);
        return document.RootElement.GetProperty("region").GetString()!;
    }
}
