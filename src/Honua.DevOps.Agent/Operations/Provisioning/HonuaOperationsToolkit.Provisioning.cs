using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.DevOps.Agent.Operations.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    private static readonly Regex RegionPattern = new(
        "^[a-z]{2}(?:-gov)?-[a-z]+-[0-9]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NamePrefixPattern = new(
        "^[a-z][a-z0-9-]{0,30}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // `terraform show` renders each planned change as e.g.
    //   "  # aws_ecs_service.honua will be updated in-place"
    //   "  # aws_db_instance.honua must be replaced"
    //   "  # aws_s3_bucket.logs will be destroyed"
    private static readonly Regex PlanResourceChangePattern = new(
        @"^\s*#\s+(?<address>[^\s]+)\s+(?<verb>will be created|will be updated in-place|will be destroyed|must be replaced|will be replaced|has moved|will be read)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlanSummaryPattern = new(
        @"Plan:\s+(?<add>[0-9]+)\s+to add,\s+(?<change>[0-9]+)\s+to change,\s+(?<destroy>[0-9]+)\s+to destroy\.",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> AllowedAwsEcsVariables = new(StringComparer.Ordinal)
    {
        "region",
        "environment",
        "name_prefix",
        "honua_image",
        "task_cpu_architecture",
        "db_publicly_accessible",
        "enable_postgis",
        "redis_enabled",
        "desired_count",
        "max_capacity",
        "deployment_mode",
        "canary_enabled",
        "alb_deletion_protection",
        "alb_access_logs_enabled",
        "alb_access_logs_force_destroy",
        "tags"
    };

    private static readonly HashSet<string> SecretVariableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "honua_admin_password",
        "honua_connection_encryption_master_key",
        "db_password",
        "existing_db_connection_string",
        "redis_connection_string"
    };

    [Description("Plan, apply, or break-glass destroy an allowlisted honua-iac stack through the governed exact-plan substrate, with plan-before-mutation and approval gates.")]
    public async Task<OperationResponse> ProvisionInfrastructureAsync(
        string stack,
        string size,
        string action,
        string variablesJson,
        bool confirmed,
        string confirmation,
        string approvalReceiptJson = "",
        CancellationToken cancellationToken = default)
    {
        string normalizedStack = NormalizeToken(stack);
        string normalizedSize = NormalizeToken(size);
        string normalizedAction = NormalizeToken(action);

        if (!ProvisioningStackCatalog.TryResolve(normalizedStack, out ProvisioningStack? provisioningStack))
        {
            return ProvisioningRefusal(
                "unsupported-stack",
                $"Stack `{normalizedStack}` is not available. 2026.1 allows only: {string.Join(", ", ProvisioningStackCatalog.Ids)}.",
                ["Choose stack=aws-ecs. AWS serverless and Helm remain sequenced follow-ups."],
                ["Never resolve a caller-controlled stack directly to a filesystem path."]);
        }

        string canonicalStack = provisioningStack!.Id;
        if (!provisioningStack.Sizes.Contains(normalizedSize))
        {
            return ProvisioningRefusal(
                "unsupported-size",
                $"Size `{normalizedSize}` is not available for `{canonicalStack}`. Allowed: {string.Join(", ", provisioningStack.Sizes)}.",
                ["Choose size=small or plan a reviewed custom topology outside this tool."],
                ["Unreviewed size values could bypass the single-node development contract."]);
        }

        string canonicalSize = normalizedSize;

        if (normalizedAction is not ("plan" or "apply" or "destroy"))
        {
            return ProvisioningRefusal(
                "invalid-action",
                "Action must be `plan`, `apply`, or `destroy`.",
                ["Use plan first. Request apply or destroy only after reviewing the returned plan."],
                []);
        }

        if (runtime.ExecutionTier == ExecutionTier.Observe)
        {
            return ProvisioningRefusal(
                "tier-gated",
                "Terraform init/plan is unavailable at the observe tier.",
                ["Register the operator at execution tier plan or higher."],
                ["Terraform init writes local provider metadata even when cloud state is not mutated."]);
        }

        // The `terraform` executable is a runtime prerequisite this process does not ship.
        // Checking for it up front turns an opaque "process did not start" Win32 failure
        // into an actionable refusal — notably for the published MCP container, whose
        // chiseled final image contains only the operator binary.
        if (!(provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance).CanRun("terraform"))
        {
            return ProvisioningRefusal(
                "terraform-unavailable",
                "The `terraform` executable was not found on PATH. No process was started.",
                [
                    "Install Terraform on the host running honua-devops and ensure it is on PATH.",
                    "In the published MCP container, mount a Terraform binary and the honua-iac checkout, e.g. "
                        + "`-v /usr/local/bin/terraform:/usr/local/bin/terraform:ro -v /path/to/honua-iac:/honua-iac:ro "
                        + "-e PATH=/usr/local/bin:/usr/bin:/bin -e HONUA_DEVOPS_TERRAFORM_LOCAL_PATH=/honua-iac` "
                        + "(see docs/QUICKSTART-MCP.md).",
                    "The container image deliberately does not redistribute the Terraform binary."
                ],
                ["Provisioning is fail-closed when the Terraform runtime cannot be proven present."]);
        }

        if (!TryResolveSubstrate(out TerraformExactSubstrate? substrate, out OperationResponse? substrateRefusal))
        {
            return substrateRefusal!;
        }

        string terraformRoot;
        try
        {
            terraformRoot = substrate!.ResolveQualifiedRoot(provisioningStack.TerraformRootName);
        }
        catch (InvalidOperationException exception)
        {
            return ProvisioningRefusal(
                "terraform-root-invalid",
                Redaction.Scrub(exception.Message),
                ["Set HONUA_DEVOPS_TERRAFORM_LOCAL_PATH to a current honua-iac checkout."],
                ["Provisioning is fail-closed when the deployable root cannot be proven."]);
        }

        Dictionary<string, object?> variables;
        try
        {
            variables = ParseAwsEcsSmallVariables(variablesJson);
        }
        catch (InvalidOperationException exception)
        {
            return ProvisioningRefusal(
                "variables-invalid",
                Redaction.Scrub(exception.Message),
                ["Provide only non-secret allowlisted values in variablesJson."],
                ["Secret-shaped and unknown variables are rejected before any process starts."]);
        }

        string environment = (string)variables["environment"]!;
        if (!runtime.AllowedEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase))
        {
            return ProvisioningRefusal(
                "environment-not-allowed",
                $"Environment `{environment}` is not in HONUA_DEVOPS_ALLOWED_ENVIRONMENTS.",
                ["Choose an explicitly allowed non-production environment."],
                ["The environment allowlist is an operator-controlled boundary."]);
        }

        if (normalizedAction is "apply" or "destroy")
        {
            OperationResponse? mutationRefusal = AuthorizeTerraformMutation(normalizedAction, environment);
            if (mutationRefusal is not null)
            {
                return mutationRefusal;
            }

            if (normalizedAction == "apply" || confirmed)
            {
                string loadError = "A reviewed saved plan token is required.";
                if (!confirmed || !TryLoadSavedPlan(
                        confirmation,
                        normalizedAction,
                        canonicalStack,
                        canonicalSize,
                        environment,
                        terraformRoot,
                        approvalReceiptJson,
                        out SavedTerraformPlan? savedPlan,
                        out ProvisionApprovalReceipt? approvalReceipt,
                        out loadError))
                {
                    return ProvisioningRefusal(
                        "confirmation-required",
                        loadError,
                        [
                            normalizedAction == "apply"
                                ? "Run action=plan, review the returned plan, then repeat action=apply with its exact tokenized confirmation challenge."
                                : "Run action=destroy with confirmed=false, review the returned destroy plan, then repeat with its exact tokenized confirmation challenge."
                        ],
                        [normalizedAction == "destroy" ? "Destroy can permanently remove data." : "Apply creates or changes billable cloud resources."]);
                }

                return await ApplySavedPlanAsync(
                    substrate!,
                    provisioningStack,
                    savedPlan!,
                    approvalReceipt!,
                    normalizedAction,
                    cancellationToken);
            }
        }

        if (!HasSecretInput(terraformRoot))
        {
            return ProvisioningRefusal(
                "secret-input-required",
                "Required Terraform secrets are not configured. No process was started.",
                [
                    "Create the gitignored examples/aws/terraform.tfvars from its committed example, or set both TF_VAR_honua_admin_password and TF_VAR_honua_connection_encryption_master_key.",
                    "Resolve secret-store references outside this tool; variablesJson never accepts secret material."
                ],
                ["Terraform cannot plan this root without the required secret inputs."]);
        }

        CleanupExpiredSavedPlans();
        string planDirectory = CreatePlanDirectory(out string planToken);
        string provisioningOperationId = $"urn:honua:provisioning:{planToken}";
        string variableFile = Path.Combine(planDirectory, "small.auto.tfvars.json");
        string planFile = Path.Combine(planDirectory, "honua.tfplan");
        string planMetadataFile = planFile + ".metadata.json";
        bool keepPlanDirectory = false;
        try
        {
            await File.WriteAllTextAsync(
                variableFile,
                JsonSerializer.Serialize(variables, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            IProvisioningProcessRunner runner = provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance;
            List<OperationBackendStep> steps = [];

            // The substrate owns init/backend resolution, the short-lived-identity
            // check, the input digest, the state read, and the saved-plan binding.
            // honua-devops supplies the root, the action, the inputs and the actor,
            // and consumes the metadata document the wrapper writes.
            List<string> planArguments =
            [
                substrate!.PlanScript,
                "--root", terraformRoot,
                "--action", normalizedAction == "destroy" ? "destroy" : "apply",
                "--plan-out", planFile,
                "--metadata-out", planMetadataFile,
                "--var-file", variableFile,
                "--actor", $"honua-devops:{provisioningOperationId}",
                "--target-id", ResolveTargetId(canonicalStack, environment),
                "--expires-in", SavedPlanLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ];

            ProvisioningProcessResult planResult = await runner.RunAsync(
                "bash",
                planArguments,
                substrate.IacRoot,
                TimeSpan.FromMinutes(20),
                SubstrateEnvironment(requireApproval: false),
                cancellationToken);
            steps.Add(ToBackendStep("terraform-exact-plan", canonicalStack, planResult, mutatesState: false));
            if (!planResult.Succeeded)
            {
                // A typed refusal is a governed decision with a specific cause and a
                // specific fix; it must not be flattened into "terraform failed".
                if (TerraformExactRefusal.TryParse(planResult, out TerraformExactRefusal? refusal))
                {
                    return SubstrateRefusal(refusal!, "plan", steps);
                }

                return TerraformFailure("terraform-plan-failed", "Terraform plan failed; apply was not attempted.", planResult, steps);
            }

            string planSummary = ReadPlanSummary(planResult.StandardOutput);
            if (!File.Exists(planFile) || !File.Exists(planMetadataFile))
            {
                return TerraformFailure(
                    "terraform-plan-artifact-missing",
                    "The exact-plan wrapper reported success but did not produce the saved-plan/metadata pair; apply is blocked.",
                    new ProvisioningProcessResult(1, string.Empty, "saved plan or metadata artifact missing", false),
                    steps);
            }

            ExactPlanMetadata? planMetadata;
            string metadataError;
            try
            {
                if (!ExactPlanMetadata.TryRead(
                        await File.ReadAllTextAsync(planMetadataFile, cancellationToken),
                        await File.ReadAllTextAsync(substrate.ExactPlanSchemaPath, cancellationToken),
                        out planMetadata,
                        out metadataError))
                {
                    return ProvisioningRefusal(
                        "exact-plan-metadata-invalid",
                        Redaction.Scrub(metadataError),
                        ["Update the honua-iac checkout so its wrapper and published schema agree."],
                        ["An unreadable plan binding cannot be approved; no apply was started."]);
                }
            }
            catch (IOException exception)
            {
                return ProvisioningRefusal(
                    "exact-plan-metadata-invalid",
                    Redaction.Scrub(exception.Message),
                    ["Confirm the honua-iac checkout ships `infrastructure/terraform/contracts`."],
                    []);
            }

            // Reviewable evidence. `terraform show` against the SAVED PLAN is read-only and
            // is the only way an MCP caller can see which resources, replacements, and
            // deletions it is being asked to confirm. Without it the response tells the
            // caller to "review the complete plan" while handing it only three numbers.
            ExactPlanMetadata metadata = planMetadata!;

            ProvisioningProcessResult showResult = await runner.RunAsync(
                "terraform",
                ["show", "-no-color", planFile],
                terraformRoot,
                TimeSpan.FromMinutes(5),
                environment: null,
                cancellationToken);
            steps.Add(ToBackendStep("terraform-show", canonicalStack, showResult, mutatesState: false));
            TerraformPlanReview planReview = BuildPlanReview(showResult);

            bool destroyPlan = normalizedAction == "destroy";
            ProtectSavedPlan(planFile);
            SavePlanManifest(
                planDirectory,
                new SavedTerraformPlanManifest(
                    planToken,
                    provisioningOperationId,
                    canonicalStack,
                    canonicalSize,
                    environment,
                    terraformRoot,
                    DateTimeOffset.UtcNow,
                    destroyPlan,
                    planSummary,
                    ComputeSha256(planFile),
                    provisioningStack.TerraformRootName,
                    planMetadataFile,
                    metadata));
            keepPlanDirectory = true;
            string nextAction = destroyPlan ? "destroy" : "apply";
            string challenge = $"{nextAction}:{canonicalStack}:{environment}:{planToken}";

            return new OperationResponse(
                Status: destroyPlan ? "terraform-destroy-plan-ready" : "terraform-plan-ready",
                Summary: destroyPlan
                    ? $"Terraform destroy plan ready for `{canonicalStack}` in `{environment}`: {planSummary}"
                    : $"Terraform plan ready for `{canonicalStack}` size `{canonicalSize}` in `{environment}`: {planSummary}",
                Findings:
                [
                    $"Deployable root: {terraformRoot}.",
                    $"Saved plan token: {planToken} (expires in {SavedPlanLifetimeSeconds / 60} minutes).",
                    "Planned through the honua-iac governed exact-plan substrate (`scripts/terraform-exact-plan.sh`); no hand-rolled Terraform argv was used.",
                    "Generated variables were restricted to the non-secret aws-ecs/small allowlist.",
                    // The value an approval must bind to. Without it in the response the
                    // caller cannot issue an approval the apply wrapper will accept.
                    $"Approval binds to plan_metadata_digest: {metadata.PlanMetadataDigest}.",
                    $"Saved plan sha256: {metadata.SavedPlanSha256}.",
                    $"Backend: {metadata.BackendKind} (remote={metadata.BackendIsRemote}) workspace `{metadata.Workspace}`, backend_config_digest {metadata.BackendConfigDigest}.",
                    $"Prior state lineage {metadata.StateLineageBefore ?? "(none)"} serial {metadata.StateSerialBefore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}.",
                    $"Execution identity: {metadata.AssumedRoleArn} in account {metadata.AccountId} ({metadata.CredentialKind}).",
                    $"IaC revision {metadata.IacRevision}, Terraform {metadata.TerraformVersion}, provider lock {metadata.ProviderLockDigest}.",
                    $"Release qualified: {metadata.ReleaseQualified.ToString().ToLowerInvariant()} (evidence mode: {metadata.EvidenceMode}).",
                    $"Planned resource changes ({planReview.ChangeCount} shown{(planReview.Truncated ? ", truncated" : string.Empty)}):",
                    .. planReview.ResourceChanges,
                    planReview.DestructiveChanges.Count == 0
                        ? "No replacements or deletions are present in this plan."
                        : $"DESTRUCTIVE changes in this plan: {string.Join("; ", planReview.DestructiveChanges)}.",
                    $"Redacted plan digest (sha256 of the reviewed `terraform show` output): {planReview.ReviewDigest}."
                ],
                Actions:
                [
                    "Review the planned resource changes listed above before mutation; replacements and deletions are called out explicitly.",
                    $"Issue a honua.devops.provision-approval/v1 receipt binding planMetadataDigest={metadata.PlanMetadataDigest}.",
                    $"Repeat with action={nextAction}, confirmed=true, and confirmation={challenge}. The saved plan, not a newly generated plan, will be applied.",
                    metadata.ReleaseQualified
                        ? "This plan is release-qualified; retain the metadata digest as approval evidence."
                        : "This plan is NOT release-qualified and the apply wrapper will refuse it with `unqualified-plan-refused`."
                ],
                ValidationChecks:
                [
                    "exact-plan wrapper succeeded",
                    "plan metadata validated against terraform-exact-plan.v1.schema.json",
                    destroyPlan ? "terraform destroy plan succeeded" : "terraform plan succeeded",
                    "saved-plan artifact exists and is token-bound",
                    "no apply process started"
                ],
                Risks:
                [
                    $"The saved plan expires at {metadata.ExpiresAtUtc:O} and the apply wrapper refuses it afterwards with `plan-expired`.",
                    "Saved Terraform plans can contain sensitive values; this artifact is kept in the current user's protected temporary directory and deleted after use or expiry.",
                    metadata.IsOfflineEvidence
                        ? "This plan was produced in HONUA_IAC_OFFLINE fixture mode and is stamped `offline-test`; it is not evidence about any real cloud account."
                        : "State substitution or drift between now and apply is refused by the substrate, not silently reconciled."
                ],
                BackendSteps: steps,
                ProvisioningLineage: new ProvisioningLineage(
                    provisioningOperationId,
                    PlanSha256: ComputeSha256(planFile),
                    PlanMetadataDigest: metadata.PlanMetadataDigest));
        }
        finally
        {
            if (!keepPlanDirectory)
            {
                DeletePlanDirectory(planDirectory);
            }
        }
    }

    [Description("Write a secretless Honua CLI/MCP handoff from the stack's honua.operator-contract/v1 endpoint and admin-key secret reference.")]
    public async Task<OperationResponse> InstallHandoffAsync(
        string stack,
        string baseUrl,
        string adminKeySecretRef,
        string outputDirectory,
        bool overwrite,
        string provisioningOperationId = "",
        CancellationToken cancellationToken = default)
    {
        const string canonicalStack = "aws-ecs";
        if (!string.Equals(NormalizeToken(stack), canonicalStack, StringComparison.Ordinal))
        {
            return ProvisioningRefusal(
                "unsupported-stack",
                "install_handoff currently supports stack=aws-ecs only.",
                ["Choose the stack that was provisioned by the 2026.1 aws-ecs lane."],
                []);
        }

        List<OperationBackendStep> steps = [];

        string rootProvisioningOperationId = provisioningOperationId.Trim();
        if (!rootProvisioningOperationId.StartsWith("urn:honua:provisioning:", StringComparison.Ordinal)
            || rootProvisioningOperationId.Length > 200)
        {
            return ProvisioningRefusal(
                "provisioning-operation-required",
                "install_handoff requires the stable provisioningOperationId returned by the plan/apply lineage.",
                ["Pass the exact provisioningOperationId; do not invent or substitute a server operation id."],
                []);
        }
        if (!TryLoadProvisioningState(rootProvisioningOperationId, out ProvisioningState? provisioningState)
            || provisioningState!.Stack != canonicalStack
            || provisioningState.Action != "apply")
        {
            return ProvisioningRefusal(
                "provisioning-evidence-missing",
                "No DevOps-produced successful apply evidence exists for this provisioningOperationId.",
                ["Run the exact governed plan and apply before generating the install handoff."],
                ["Caller-authored provisioning bindings are not accepted."]);
        }

        // The endpoint and the admin-key locator come from the stack's own
        // honua.operator-contract/v1 projection, captured at apply time. A caller
        // argument is an OVERRIDE — still accepted, because a development cell may
        // legitimately sit behind a tunnel, but recorded as an override in the
        // handoff and the binding so no reader can mistake it for what the stack
        // reported.
        string contractEndpoint = provisioningState.Endpoint ?? string.Empty;
        string contractSecretRef = provisioningState.AdminKeySecretRef ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contractEndpoint) || string.IsNullOrWhiteSpace(contractSecretRef))
        {
            return ProvisioningRefusal(
                "operator-contract-missing",
                "The recorded apply evidence carries no operator-contract endpoint and admin-key secret reference.",
                [
                    "Re-run the governed apply against a root that projects honua.operator-contract/v1.",
                    "Provisioning evidence produced before contract consumption cannot back a handoff."
                ],
                ["A handoff built from caller-supplied identity is not evidence about the deployed stack."]);
        }

        string callerBaseUrl = baseUrl.Trim();
        string callerSecretRef = adminKeySecretRef.Trim();
        bool endpointOverridden = callerBaseUrl.Length > 0
            && !string.Equals(callerBaseUrl.TrimEnd('/'), contractEndpoint.TrimEnd('/'), StringComparison.Ordinal);
        bool secretRefOverridden = callerSecretRef.Length > 0
            && !string.Equals(callerSecretRef, contractSecretRef, StringComparison.Ordinal);

        string normalizedBaseUrl = endpointOverridden ? callerBaseUrl : contractEndpoint;
        string normalizedSecretRef = secretRefOverridden ? callerSecretRef : contractSecretRef;

        if (!TryValidateBaseUrl(normalizedBaseUrl, out Uri? parsedBaseUrl, out string urlError))
        {
            return ProvisioningRefusal(
                "base-url-invalid",
                urlError,
                ["Use an absolute HTTPS URL, or HTTP only for a loopback development endpoint."],
                ["An untrusted proxy endpoint could receive the resolved admin credential at launch time."]);
        }

        if (!IsSecretReference(normalizedSecretRef))
        {
            return ProvisioningRefusal(
                "secret-ref-invalid",
                "adminKeySecretRef must be an AWS Secrets Manager ARN/URI, Azure Key Vault URI, or secret:// reference. Secret material is not accepted.",
                ["Pass the cloud secret identifier, never the admin-key value."],
                ["A literal admin key must not be persisted in the handoff files or audit journal."]);
        }

        string proxyPackage = runtime.McpProxyPackage?.Trim() ?? string.Empty;
        string proxyIntegrity = runtime.McpProxyIntegrity?.Trim() ?? string.Empty;
        string candidateReference = runtime.CandidateReference?.Trim() ?? string.Empty;
        if (!Regex.IsMatch(proxyPackage, "^@honua/mcp-server@[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(proxyIntegrity, "^sha(256|384|512)-[A-Za-z0-9+/=]+$", RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(candidateReference))
        {
            return ProvisioningRefusal(
                "proxy-pin-required",
                "The proxy package, integrity, and candidate reference must be pinned by operator configuration before handoff emission.",
                ["Set HONUA_DEVOPS_MCP_PROXY_PACKAGE, HONUA_DEVOPS_MCP_PROXY_INTEGRITY, and HONUA_DEVOPS_CANDIDATE_REFERENCE from the release manifest."],
                ["An unversioned proxy cannot produce release-grade handoff evidence."]);
        }

        string handoffDirectory;
        try
        {
            handoffDirectory = ResolveHandoffDirectory(outputDirectory, canonicalStack);
        }
        catch (InvalidOperationException exception)
        {
            return ProvisioningRefusal("handoff-path-invalid", exception.Message, [], []);
        }

        string configPath = Path.Combine(handoffDirectory, "honua-mcp-proxy.handoff.json");
        string envPath = Path.Combine(handoffDirectory, "honua.env.example");
        if (!overwrite && (File.Exists(configPath) || File.Exists(envPath)))
        {
            return ProvisioningRefusal(
                "handoff-exists",
                $"Handoff files already exist in `{handoffDirectory}`; nothing was overwritten.",
                ["Review the existing files, then repeat with overwrite=true if replacement is intended."],
                ["Overwriting client configuration can disconnect an existing environment."]);
        }

        Directory.CreateDirectory(handoffDirectory);
        Uri mcpUri = new(parsedBaseUrl!, "mcp");
        object contract = new
        {
            schemaVersion = "honua.mcp-proxy.handoff/v1",
            rootProvisioningOperationId,
            provisioningLineage = provisioningState.Lineage,
            candidateReference,
            // Provenance of the two identities that decide where a resolved admin
            // credential is sent. `operator-contract` means the stack said so;
            // `caller-override` means someone told us instead.
            endpointSource = endpointOverridden ? "caller-override" : "operator-contract",
            adminKeySecretRefSource = secretRefOverridden ? "caller-override" : "operator-contract",
            operatorContract = new
            {
                digest = provisioningState.OperatorContractDigest,
                status = provisioningState.OperatorContractStatus,
                endpoint = contractEndpoint,
                adminKeySecretRef = contractSecretRef
            },
            proxyArtifact = new { package = proxyPackage, integrity = proxyIntegrity },
            command = "npx",
            args = new[] { "-y", "--package", proxyPackage, "honua-mcp-proxy" },
            env = new Dictionary<string, string>
            {
                ["HONUA_BASE_URL"] = parsedBaseUrl!.AbsoluteUri.TrimEnd('/'),
                ["HONUA_MCP_REMOTE_URL"] = mcpUri.AbsoluteUri
            },
            secretRefs = new Dictionary<string, string>
            {
                ["HONUA_ADMIN_KEY"] = normalizedSecretRef
            },
            capabilityContract = new
            {
                verification = new
                {
                    method = "MCP tools/list",
                    failClosed = true
                },
                required = new object[]
                {
                    new
                    {
                        name = "admin",
                        activation = "default operation family",
                        serverConfiguration = Array.Empty<string>(),
                        requiredToolPrefixes = new[] { "honua_admin_" },
                        requiredTools = new[] { "honua_admin_server_status" }
                    },
                    new
                    {
                        name = "analysis",
                        activation = "Mcp__Profiles__1=analysis",
                        serverConfiguration = new[] { "Mcp__Profiles__1=analysis" },
                        requiredToolPrefixes = Array.Empty<string>(),
                        requiredTools = new[]
                        {
                            "honua_buffer_features",
                            "honua_overlay_features",
                            "honua_summarize_statistics",
                            "honua_reproject_features",
                            "honua_join_features",
                            "honua_export_dataset"
                        }
                    },
                    new
                    {
                        name = "esri-gp",
                        activation = "Mcp__Profiles__2=esri-gp",
                        serverConfiguration = new[] { "Mcp__Profiles__2=esri-gp" },
                        requiredToolPrefixes = Array.Empty<string>(),
                        requiredTools = new[]
                        {
                            "honua_esri_gp_list_tasks",
                            "honua_esri_gp_describe_task",
                            "honua_esri_gp_execute_task"
                        }
                    }
                }
            }
        };
        string configBytes = JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

        // Validated before it is written. The handoff is the document a client will
        // resolve a live admin credential against, so it must satisfy its published
        // contract or not exist at all.
        IReadOnlyList<string> handoffErrors = ProvisioningContracts.ValidateProxyHandoff(configBytes);
        if (handoffErrors.Count > 0)
        {
            return ProvisioningRefusal(
                "handoff-contract-invalid",
                "The generated handoff does not satisfy honua-mcp-proxy-handoff.v1.schema.json: "
                    + string.Join("; ", handoffErrors.Take(8)),
                ["This is a defect in honua-devops, not in the operator's environment; report it with this status."],
                ["No handoff files were written."]);
        }

        await File.WriteAllTextAsync(
            configPath,
            configBytes,
            cancellationToken);
        await File.WriteAllTextAsync(
            envPath,
            $"HONUA_BASE_URL={parsedBaseUrl.AbsoluteUri.TrimEnd('/')}" + Environment.NewLine
                + $"HONUA_MCP_REMOTE_URL={mcpUri.AbsoluteUri}" + Environment.NewLine
                + $"# Resolve HONUA_ADMIN_KEY at launch from: {normalizedSecretRef}" + Environment.NewLine
                + "# HONUA_ADMIN_KEY is intentionally absent; never paste it into this file." + Environment.NewLine
                + "# Required server capabilities: admin (default-on), analysis (Mcp__Profiles__1=analysis), esri-gp (Mcp__Profiles__2=esri-gp)." + Environment.NewLine
                + "# Fail closed unless MCP tools/list exposes the required tools recorded in honua-mcp-proxy.handoff.json." + Environment.NewLine,
            cancellationToken);
        steps.Add(new OperationBackendStep(
            "write-install-handoff",
            configPath,
            true,
            "Wrote a secretless versioned CLI/MCP handoff.",
            "HONUA_ADMIN_KEY=<secret-reference-only>",
            MutatesState: true));

        return new OperationResponse(
            Status: "install-handoff-written",
            Summary: $"Secretless Honua CLI/MCP handoff written but not yet verified for `{canonicalStack}` at `{handoffDirectory}`.",
            Findings:
            [
                $"HONUA_BASE_URL={parsedBaseUrl.AbsoluteUri.TrimEnd('/')} (source: {(endpointOverridden ? "caller-override" : "operator-contract")}).",
                $"HONUA_ADMIN_KEY secret reference={normalizedSecretRef} (source: {(secretRefOverridden ? "caller-override" : "operator-contract")}).",
                $"Operator contract {provisioningState.OperatorContractStatus} digest {provisioningState.OperatorContractDigest}; it reports endpoint {contractEndpoint}.",
                endpointOverridden || secretRefOverridden
                    ? "WARNING: a caller argument overrode what the stack reported. The override is recorded in the handoff and the binding."
                    : "Endpoint and admin-key locator were taken from the stack's operator contract, not from caller arguments.",
                $"HONUA_MCP_REMOTE_URL={mcpUri.AbsoluteUri}",
                $"Proxy handoff: {configPath}",
                $"Proxy artifact: {proxyPackage} ({proxyIntegrity}).",
                $"Root provisioning operation: {rootProvisioningOperationId}.",
                "Required AI capability families: admin, analysis, and esri-gp.",
                "No admin-key material was read, returned, or written."
            ],
            Actions:
            [
                "Resolve the secret reference into HONUA_ADMIN_KEY only in the client process environment.",
                "Register honua-mcp-proxy with the command/args/env contract and run a readiness plus MCP tools/list probe.",
                "Fail the installation check if the admin family, analysis tools, or Esri GP tools in capabilityContract are absent.",
                "Keep the secret-store access policy scoped to the operator identity and this one secret."
            ],
            ValidationChecks:
            [
                "base URL is HTTPS or loopback HTTP",
                "admin key input is a reference rather than material",
                "proxy configuration contains HONUA_MCP_REMOTE_URL",
                "proxy configuration names the required admin, analysis, and esri-gp tool contract",
                "handoff files contain no HONUA_ADMIN_KEY value"
            ],
            Risks:
            [
                "The handoff is not a health certificate; verify the deployed server and proxy after resolving the secret.",
                "A broad secret-store identity could expose unrelated credentials even though this file is secretless."
            ],
            BackendSteps: steps,
            ProvisioningLineage: provisioningState.Lineage with
            {
                HandoffReceiptSha256 = ComputeSha256(Encoding.UTF8.GetBytes(configBytes)),
                RootProvisioningOperationId = rootProvisioningOperationId
            });
    }

    [Description("Run the exact emitted proxy handoff, resolve its secret reference only into the child process, and produce a content-addressed verified provision binding.")]
    public async Task<OperationResponse> VerifyInstallHandoffAsync(
        string handoffConfigPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(handoffConfigPath.Trim());
        if (!File.Exists(fullPath))
        {
            return ProvisioningRefusal("handoff-config-missing", "The emitted handoff configuration does not exist.", [], []);
        }

        InstallHandoffVerificationRequest request;
        string configBytes;
        try
        {
            configBytes = await File.ReadAllTextAsync(fullPath, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(configBytes);
            JsonElement root = document.RootElement;
            string ReadRequired(string name) => root.GetProperty(name).GetString()
                ?? throw new InvalidOperationException($"Handoff field `{name}` is empty.");
            string command = ReadRequired("command");
            string[] arguments = root.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Dictionary<string, string> environment = root.GetProperty("env").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
            string secretReference = root.GetProperty("secretRefs").GetProperty("HONUA_ADMIN_KEY").GetString()!;
            JsonElement proxy = root.GetProperty("proxyArtifact");
            string proxyPackage = proxy.GetProperty("package").GetString()!;
            string proxyIntegrity = proxy.GetProperty("integrity").GetString()!;
            List<string> requiredTools = [];
            foreach (JsonElement family in root.GetProperty("capabilityContract").GetProperty("required").EnumerateArray())
            {
                requiredTools.AddRange(family.GetProperty("requiredTools").EnumerateArray().Select(value => value.GetString()!));
            }
            request = new InstallHandoffVerificationRequest(
                command,
                arguments,
                environment,
                secretReference,
                environment["HONUA_BASE_URL"],
                ReadRequired("candidateReference"),
                proxyPackage,
                proxyIntegrity,
                ReadRequired("rootProvisioningOperationId"),
                requiredTools.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return ProvisioningRefusal("handoff-config-invalid", Redaction.Scrub(exception.Message), [], []);
        }

        if (!string.Equals(request.ProxyPackage, runtime.McpProxyPackage, StringComparison.Ordinal)
            || !string.Equals(request.ProxyIntegrity, runtime.McpProxyIntegrity, StringComparison.Ordinal)
            || !string.Equals(request.CandidateReference, runtime.CandidateReference, StringComparison.Ordinal))
        {
            return ProvisioningRefusal(
                "handoff-pin-mismatch",
                "The handoff candidate/proxy pins do not match the current operator release configuration.",
                ["Regenerate the handoff from the current manifest pins."],
                []);
        }
        if (!TryLoadProvisioningState(request.ProvisioningOperationId, out ProvisioningState? state))
        {
            return ProvisioningRefusal(
                "provisioning-evidence-missing",
                "The handoff cannot be joined to DevOps-produced apply evidence.",
                [], []);
        }

        IInstallHandoffVerifier verifier = installHandoffVerifier ?? SystemInstallHandoffVerifier.Instance;
        InstallHandoffVerificationResult verification = await verifier.VerifyAsync(request, cancellationToken);
        if (!verification.Succeeded)
        {
            return new OperationResponse(
                verification.Status,
                verification.Detail,
                ["No verified handoff receipt or provision binding was emitted."],
                ["Correct the reported health/auth/proxy/roster failure and rerun verification."],
                ["verification failed closed"],
                ["Partial verification is not installation readiness."],
                BackendSteps: verification.Steps,
                ProvisioningLineage: state!.Lineage);
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        string receiptPath = Path.Combine(directory, "honua-install-verification.receipt.json");
        string bindingPath = Path.Combine(directory, "honua-devops-aws-ecs-provision-binding.json");
        if (!overwrite && (File.Exists(receiptPath) || File.Exists(bindingPath)))
        {
            return ProvisioningRefusal("verification-evidence-exists", "Verification evidence already exists; nothing was overwritten.", [], []);
        }

        string handoffSha = ComputeSha256(Encoding.UTF8.GetBytes(configBytes));
        string verificationCore = JsonSerializer.Serialize(new
        {
            schemaVersion = "honua.devops.install-handoff-verification/v1",
            provisioningOperationId = request.ProvisioningOperationId,
            handoffSha256 = handoffSha,
            candidateReference = request.CandidateReference,
            proxyPackage = request.ProxyPackage,
            proxyIntegrity = request.ProxyIntegrity,
            secretReferenceSha256 = ComputeSha256(Encoding.UTF8.GetBytes(request.AdminKeySecretReference)),
            serverIdentity = verification.ServerIdentity,
            observedTools = verification.ObservedTools,
            verifiedAtUtc = DateTimeOffset.UtcNow
        });
        string verificationSha = ComputeSha256(Encoding.UTF8.GetBytes(verificationCore));
        string verificationId = $"urn:sha256:{verificationSha}";
        string receiptBytes = JsonSerializer.Serialize(new
        {
            receiptId = verificationId,
            receiptSha256 = verificationSha,
            evidence = JsonSerializer.Deserialize<JsonElement>(verificationCore)
        }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

        ProvisioningLineage completedLineage = state!.Lineage with
        {
            HandoffReceiptSha256 = handoffSha,
            HandoffVerificationReceiptId = verificationId,
            HandoffVerificationReceiptSha256 = verificationSha,
            RootProvisioningOperationId = request.ProvisioningOperationId
        };
        if (state.Execution is null)
        {
            return ProvisioningRefusal(
                "iac-execution-evidence-missing",
                "The recorded apply evidence carries no honua-iac execution facts, so a binding could not name the "
                    + "state, backend and identity that produced the claim.",
                ["Re-run the governed plan and apply through the exact-plan substrate."],
                ["A binding that cannot name its state lineage is not evidence."]);
        }

        // Provenance carried forward from the handoff: a reader of the binding must be
        // able to tell whether the endpoint it names is what the stack reported.
        (string endpointSource, string secretRefSource) = ReadHandoffSources(configBytes);

        string bindingBytes = JsonSerializer.Serialize(new
        {
            schemaVersion = "honua.devops.aws-ecs-provision-binding/v1",
            lineage = completedLineage,
            endpoint = request.BaseUrl,
            endpointSource,
            adminKeySecretRefSource = secretRefSource,
            candidateReference = request.CandidateReference,
            proxyArtifact = new { package = request.ProxyPackage, integrity = request.ProxyIntegrity },
            secretReferenceSha256 = ComputeSha256(Encoding.UTF8.GetBytes(request.AdminKeySecretReference)),
            handoffSha256 = handoffSha,
            verificationReceiptId = verificationId,
            verificationReceiptSha256 = verificationSha,
            operatorContract = new
            {
                digest = state.OperatorContractDigest,
                status = state.OperatorContractStatus,
                endpoint = state.Endpoint
            },
            // Which state, under which identity, produced this claim.
            iacExecution = state.Execution,
            execReceiptSha256 = state.ExecReceiptSha256,
            execReceipt = state.ExecReceipt,
            teardownHandle = state.TeardownHandle
        }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

        // Validated against the published contract BEFORE it is written: an evidence
        // artifact that does not satisfy its own schema must never reach the disk,
        // because everything downstream treats its presence as the claim.
        IReadOnlyList<string> bindingErrors = ProvisioningContracts.ValidateProvisionBinding(bindingBytes);
        if (bindingErrors.Count > 0)
        {
            return ProvisioningRefusal(
                "provision-binding-invalid",
                "The provision binding does not satisfy honua-devops-aws-ecs-provision-binding.schema.json: "
                    + string.Join("; ", bindingErrors.Take(8)),
                ["This is a defect in honua-devops, not in the operator's environment; report it with this status."],
                ["No binding was written."]);
        }

        IReadOnlyList<string> receiptErrors = ProvisioningContracts.ValidateVerificationReceipt(receiptBytes);
        if (receiptErrors.Count > 0)
        {
            return ProvisioningRefusal(
                "verification-receipt-invalid",
                "The verification receipt does not satisfy honua-devops-install-handoff-verification.v1.schema.json: "
                    + string.Join("; ", receiptErrors.Take(8)),
                ["This is a defect in honua-devops, not in the operator's environment; report it with this status."],
                ["No verification evidence was written."]);
        }

        await File.WriteAllTextAsync(receiptPath, receiptBytes, cancellationToken);
        await File.WriteAllTextAsync(bindingPath, bindingBytes, cancellationToken);
        ProtectSavedPlan(receiptPath);
        ProtectSavedPlan(bindingPath);
        SaveProvisioningState(state with { Lineage = completedLineage });

        List<OperationBackendStep> steps = [.. verification.Steps,
            new("write-handoff-verification-receipt", receiptPath, true, "content-addressed receipt written", verificationId, true),
            new("write-aws-ecs-provision-binding", bindingPath, true, "DevOps-produced binding written", verificationSha, true)];
        return new OperationResponse(
            "install-handoff-verified",
            "The exact pinned proxy handoff passed health, auth, MCP roster, and Admin status verification.",
            [$"Verification receipt: {receiptPath}", $"Provision binding: {bindingPath}", $"Receipt id: {verificationId}"],
            ["Join this binding into the release-owned candidate receipt."],
            ["health ready", "Admin authentication", "MCP initialize", "paged required roster", "Admin status call"],
            ["This local verification is not a substitute for the disposable AWS live-cell acceptance run."],
            BackendSteps: steps,
            ProvisioningLineage: completedLineage);
    }

    /// <summary>
    /// Saved-plan lifetime. Passed to the substrate as <c>--expires-in</c> so the
    /// wrapper's <c>plan-expired</c> refusal and this process's own age check agree
    /// on one number instead of drifting apart.
    /// </summary>
    private const int SavedPlanLifetimeSeconds = 1800;

    private bool TryResolveSubstrate(out TerraformExactSubstrate? substrate, out OperationResponse? refusal)
    {
        refusal = null;
        if (TerraformExactSubstrate.TryResolve(runtime.TerraformLocalPath, out substrate, out string error))
        {
            return true;
        }

        refusal = ProvisioningRefusal(
            "iac-substrate-unavailable",
            Redaction.Scrub(error),
            [
                "Set HONUA_DEVOPS_TERRAFORM_LOCAL_PATH to a honua-iac checkout at or after honua-iac#158.",
                "The checkout must ship `scripts/terraform-exact-*.sh` and `infrastructure/terraform/contracts`."
            ],
            ["honua-devops does not execute Terraform outside the governed substrate."]);
        return false;
    }

    /// <summary>
    /// The environment contract honua-devops imposes on the wrappers. Approval
    /// enforcement is set here rather than inherited so it cannot be switched off by
    /// how the host process happened to be launched.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? SubstrateEnvironment(bool requireApproval)
        => requireApproval
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TerraformExactSubstrate.RequireApprovalVariable] = "1"
            }
            : null;

    private string ResolveTargetId(string stack, string environment)
        => string.IsNullOrWhiteSpace(runtime.DeployTargetId)
            ? $"{stack}:{environment}"
            : runtime.DeployTargetId!.Trim();

    /// <summary>
    /// Surfaces a wrapper refusal as its own typed status rather than as free text
    /// inside a generic Terraform failure.
    /// </summary>
    private static OperationResponse SubstrateRefusal(
        TerraformExactRefusal refusal,
        string action,
        IReadOnlyList<OperationBackendStep> steps)
    {
        return new OperationResponse(
            Status: refusal.Status,
            Summary: $"The honua-iac execution substrate refused the {action} before any mutation: "
                + $"REFUSED[{refusal.Reason}]. {refusal.Message}",
            Findings:
            [
                $"Refusal reason: {refusal.Reason}.",
                refusal.IsKnown
                    ? "This is a documented row of the fail-closed matrix in honua-iac `docs/devops/terraform-exact-plan-contract.md`."
                    : "This reason is not in the roster honua-devops knows; the honua-iac substrate may be newer than this build.",
                refusal.Message
            ],
            Actions:
            [
                "Correct the specific cause named by the refusal reason; do not retry unchanged.",
                "A refusal releases the plan claim, so the same approved plan can be retried once the cause is fixed."
            ],
            ValidationChecks: ["the substrate refused before any process mutated state"],
            Risks: ["Nothing was mutated by this call."],
            BackendSteps: steps);
    }

    private OperationResponse? AuthorizeTerraformMutation(string action, string environment)
    {
        if (runtime.ExecutionMode != ExecutionMode.Execute)
        {
            return ProvisioningRefusal(
                "execution-mode-gated",
                $"Terraform {action} requires HONUA_DEVOPS_EXECUTION_MODE=execute.",
                ["Review a plan, then re-register the operator in execute mode if mutation is intended."],
                ["Plan mode is intentionally incapable of cloud mutation."]);
        }

        if (runtime.IsProductionEnvironment(environment))
        {
            return ProvisioningRefusal(
                "production-automation-disabled",
                "2026.1 provision_infrastructure does not automate production applies or destroys.",
                ["Use the reviewed promotion/control-repo path for production."],
                ["The cloud provisioning slice is deliberately lower-environment only."]);
        }

        if (action == "destroy")
        {
            if (runtime.ExecutionTier != ExecutionTier.BreakGlass)
            {
                return ProvisioningRefusal(
                    "break-glass-required",
                    "Terraform destroy requires execution tier break-glass.",
                    ["Escalate deliberately and retain the required post-action review evidence."],
                    ["Destroy irreversibly removes cloud resources and may remove data."]);
            }
        }
        else if (runtime.ExecutionTier is not (ExecutionTier.ExecuteLowerEnv or ExecutionTier.BreakGlass))
        {
            return ProvisioningRefusal(
                "execution-tier-gated",
                "Terraform apply requires execution tier execute-lower-env or break-glass.",
                ["Review the plan, then use execute-lower-env for a non-production cell."],
                ["Planning and proposal tiers are intentionally non-mutating."]);
        }

        if (EffectivePolicy.ApprovalMode != ApprovalMode.DirectAllowed)
        {
            return ProvisioningRefusal(
                "approval-mode-gated",
                "Terraform mutation requires HONUA_DEVOPS_APPROVAL_MODE=direct-allowed in addition to the execution tier.",
                ["Keep pr-first for ordinary sessions; create a deliberately scoped direct-allowed session for the reviewed lower-environment apply."],
                ["Changing approval mode broadens the operator's mutation authority for that process."]);
        }

        return null;
    }

    private const string DefaultProvisioningEnvironment = "dev";

    private static string DefaultNamePrefixFor(string environment) => $"honua-{environment}";

    private static Dictionary<string, object?> ParseAwsEcsSmallVariables(string variablesJson)
    {
        Dictionary<string, object?> variables = new(StringComparer.Ordinal)
        {
            ["environment"] = DefaultProvisioningEnvironment,
            ["name_prefix"] = DefaultNamePrefixFor(DefaultProvisioningEnvironment),
            ["deployment_mode"] = "SingleInstance",
            ["desired_count"] = 1,
            ["max_capacity"] = 1,
            ["redis_enabled"] = true,
            ["canary_enabled"] = false,
            ["alb_deletion_protection"] = false,
            ["alb_access_logs_enabled"] = false,
            ["alb_access_logs_force_destroy"] = true
        };

        string payload = string.IsNullOrWhiteSpace(variablesJson) ? "{}" : variablesJson;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"variablesJson must be a JSON object: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("variablesJson must be a JSON object.");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (SecretVariableNames.Contains(property.Name))
                {
                    throw new InvalidOperationException($"Variable `{property.Name}` is secret-shaped and must be supplied out-of-band.");
                }
                if (!AllowedAwsEcsVariables.Contains(property.Name))
                {
                    throw new InvalidOperationException($"Variable `{property.Name}` is not in the aws-ecs/small allowlist.");
                }

                variables[property.Name] = ValidateVariable(property.Name, property.Value);
            }

            // The default name prefix is derived from the SELECTED environment, not from the
            // default one. Otherwise `environment=staging` without an explicit name_prefix
            // plans staging infrastructure under the `honua-dev` names, which collides with
            // or reconciles the existing development cell — including under a break-glass
            // destroy, where it would target the wrong cell's resources.
            bool environmentSupplied = document.RootElement.TryGetProperty("environment", out _);
            bool namePrefixSupplied = document.RootElement.TryGetProperty("name_prefix", out _);
            if (environmentSupplied && !namePrefixSupplied)
            {
                variables["name_prefix"] = DefaultNamePrefixFor((string)variables["environment"]!);
            }
        }

        return variables;
    }

    private static object ValidateVariable(string name, JsonElement value)
    {
        return name switch
        {
            "region" => ValidateString(name, value, RegionPattern),
            "environment" => ValidateEnvironment(value),
            "name_prefix" => ValidateString(name, value, NamePrefixPattern),
            "honua_image" => ValidateImage(value),
            "task_cpu_architecture" => ValidateChoice(name, value, "X86_64", "ARM64"),
            "deployment_mode" => ValidateChoice(name, value, "SingleInstance"),
            "desired_count" or "max_capacity" => ValidateExactInteger(name, value, 1),
            "redis_enabled" => ValidateExactBoolean(name, value, true),
            "db_publicly_accessible" or "canary_enabled" => ValidateExactBoolean(name, value, false),
            "enable_postgis" or "alb_deletion_protection" or "alb_access_logs_enabled" or "alb_access_logs_force_destroy" => ValidateBoolean(name, value),
            "tags" => ValidateTags(value),
            _ => throw new InvalidOperationException($"Variable `{name}` is not supported.")
        };
    }

    private static string ValidateEnvironment(JsonElement value)
    {
        string environment = ReadString("environment", value).ToLowerInvariant();
        if (environment is not ("dev" or "staging"))
        {
            throw new InvalidOperationException("Variable `environment` must be `dev` or `staging` for the small provisioning lane.");
        }
        return environment;
    }

    private static string ValidateImage(JsonElement value)
    {
        string image = ReadString("honua_image", value);
        if (image.Length > 512 || image.Any(char.IsWhiteSpace) || image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Variable `honua_image` must be a bounded immutable tag or digest and must not use `latest`.");
        }
        if (!image.Contains('@', StringComparison.Ordinal) && !image.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Variable `honua_image` must include an immutable tag or digest.");
        }
        return image;
    }

    private static string ValidateString(string name, JsonElement value, Regex pattern)
    {
        string text = ReadString(name, value);
        if (!pattern.IsMatch(text))
        {
            throw new InvalidOperationException($"Variable `{name}` has an invalid value.");
        }
        return text;
    }

    private static string ValidateChoice(string name, JsonElement value, params string[] choices)
    {
        string text = ReadString(name, value);
        string? choice = choices.FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));
        return choice ?? throw new InvalidOperationException($"Variable `{name}` must be one of: {string.Join(", ", choices)}.");
    }

    private static int ValidateExactInteger(string name, JsonElement value, int expected)
    {
        if (!value.TryGetInt32(out int actual) || actual != expected)
        {
            throw new InvalidOperationException($"Variable `{name}` must be {expected} for size=small.");
        }
        return actual;
    }

    private static bool ValidateExactBoolean(string name, JsonElement value, bool expected)
    {
        bool actual = ValidateBoolean(name, value);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Variable `{name}` must be {expected.ToString().ToLowerInvariant()} for size=small.");
        }
        return actual;
    }

    private static bool ValidateBoolean(string name, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"Variable `{name}` must be a boolean.")
        };
    }

    private static Dictionary<string, string> ValidateTags(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Variable `tags` must be an object of string values.");
        }

        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (tags.Count >= 32 || property.Name.Length is < 1 or > 128 || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Variable `tags` is limited to 32 bounded string entries.");
            }
            string tagValue = property.Value.GetString() ?? string.Empty;
            if (tagValue.Length > 256 || tagValue.Any(character => char.IsControl(character)))
            {
                throw new InvalidOperationException("Variable `tags` contains an invalid value.");
            }
            tags[property.Name] = tagValue;
        }
        return tags;
    }

    private static string ReadString(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Variable `{name}` must be a non-empty string.");
        }
        return value.GetString()!.Trim();
    }

    /// <summary>
    /// Recovers the endpoint/secret-reference provenance recorded in an emitted
    /// handoff. Absent fields read as <c>caller-override</c>: a handoff that does not
    /// state where its endpoint came from has not proved the stack reported it.
    /// </summary>
    private static (string EndpointSource, string SecretRefSource) ReadHandoffSources(string handoffJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(handoffJson);
            string Read(string name)
                => document.RootElement.TryGetProperty(name, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is "operator-contract" or "caller-override"
                        ? value.GetString()!
                        : "caller-override";
            return (Read("endpointSource"), Read("adminKeySecretRefSource"));
        }
        catch (JsonException)
        {
            return ("caller-override", "caller-override");
        }
    }

    private static bool HasSecretInput(string terraformRoot)
    {
        if (File.Exists(Path.Combine(terraformRoot, "terraform.tfvars")))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TF_VAR_honua_admin_password"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TF_VAR_honua_connection_encryption_master_key"));
    }

    private async Task<OperationResponse> ApplySavedPlanAsync(
        TerraformExactSubstrate substrate,
        ProvisioningStack provisioningStack,
        SavedTerraformPlan savedPlan,
        ProvisionApprovalReceipt approvalReceipt,
        string action,
        CancellationToken cancellationToken)
    {
        IProvisioningProcessRunner runner = provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance;
        List<OperationBackendStep> steps = [];
        string receiptPath = Path.Combine(savedPlan.Directory, "exec-receipt.json");
        try
        {
            // The apply wrapper re-derives every bound fact from the live context and
            // refuses before any mutation. It never regenerates a plan and never
            // accepts variables, so the approved artifact is the only thing consumed.
            ProvisioningProcessResult applyResult = await runner.RunAsync(
                "bash",
                [
                    substrate.ApplyScript,
                    "--plan", savedPlan.PlanFile,
                    "--metadata", savedPlan.Manifest.PlanMetadataFile,
                    "--approved-digest", savedPlan.Manifest.PlanMetadataDigest,
                    "--action", action == "destroy" ? "destroy" : "apply",
                    "--receipt-out", receiptPath
                ],
                substrate.IacRoot,
                TimeSpan.FromMinutes(45),
                SubstrateEnvironment(requireApproval: true),
                cancellationToken);
            steps.Add(ToBackendStep(
                action == "destroy" ? "terraform-exact-destroy" : "terraform-exact-apply",
                savedPlan.Manifest.Stack,
                applyResult,
                mutatesState: true));

            if (!applyResult.Succeeded)
            {
                // A refusal happens BEFORE any mutation. Reporting it as an apply
                // failure would tell the operator to inspect state for partial
                // changes that cannot exist.
                if (TerraformExactRefusal.TryParse(applyResult, out TerraformExactRefusal? refusal))
                {
                    return SubstrateRefusal(refusal!, action, steps);
                }

                return TerraformFailure(
                    action == "destroy" ? "terraform-destroy-failed" : "terraform-apply-failed",
                    action == "destroy"
                        ? "The previously reviewed destroy plan failed during apply."
                        : "The previously reviewed Terraform plan failed during apply.",
                    applyResult,
                    steps);
            }

            if (!File.Exists(receiptPath))
            {
                return TerraformFailure(
                    "exec-receipt-missing",
                    "The apply wrapper exited zero but wrote no execution receipt; no provisioning evidence was recorded.",
                    new ProvisioningProcessResult(1, string.Empty, "execution receipt missing", false),
                    steps);
            }

            string receiptDocument = await File.ReadAllTextAsync(receiptPath, cancellationToken);
            if (!TerraformExecReceipt.TryRead(
                    receiptDocument,
                    await File.ReadAllTextAsync(substrate.ExecReceiptSchemaPath, cancellationToken),
                    out TerraformExecReceipt? receipt,
                    out string receiptError))
            {
                return ProvisioningRefusal(
                    "exec-receipt-invalid",
                    Redaction.Scrub(receiptError),
                    ["Update the honua-iac checkout so its wrapper and published schema agree."],
                    ["The mutation may have run; inspect Terraform state before retrying."]);
            }

            if (!receipt!.Succeeded)
            {
                return TerraformFailure(
                    action == "destroy" ? "terraform-destroy-failed" : "terraform-apply-failed",
                    $"The apply wrapper recorded a failed execution (exit {receipt.ExitStatus}). "
                        + $"Resulting state lineage {receipt.StateLineageAfter} serial {receipt.StateSerialAfter}. "
                        + "The plan is spent; produce and approve a fresh plan.",
                    new ProvisioningProcessResult(receipt.ExitStatus, applyResult.StandardOutput, applyResult.StandardError, false),
                    steps);
            }

            // The approval bound one plan metadata digest; the receipt must record the
            // same one, or the receipt describes a different execution.
            if (!string.Equals(receipt.PlanMetadataDigest, savedPlan.Manifest.PlanMetadataDigest, StringComparison.Ordinal))
            {
                return ProvisioningRefusal(
                    "exec-receipt-mismatch",
                    "The execution receipt is bound to a different plan than the one that was approved.",
                    ["Do not reuse receipts across operations."],
                    ["Evidence that does not join to its approval is not evidence."]);
            }

            OperatorContract? operatorContract = null;
            if (action != "destroy" && provisioningStack.ProjectsOperatorContract)
            {
                (operatorContract, OperationResponse? contractRefusal) = await ReadOperatorContractAsync(
                    runner,
                    substrate,
                    savedPlan.Manifest.TerraformRoot,
                    steps,
                    cancellationToken);
                if (contractRefusal is not null)
                {
                    return contractRefusal;
                }
            }

            string receiptSha = ComputeSha256(Encoding.UTF8.GetBytes(receiptDocument));
            IacExecutionEvidence execution = new(
                PlanMetadataDigest: receipt.PlanMetadataDigest,
                SavedPlanSha256: receipt.SavedPlanSha256,
                TerraformRoot: savedPlan.Manifest.TerraformRootName,
                IacRevision: savedPlan.Manifest.Metadata.IacRevision,
                IacTreeDigest: savedPlan.Manifest.Metadata.IacTreeDigest,
                TerraformVersion: savedPlan.Manifest.Metadata.TerraformVersion,
                ProviderLockDigest: savedPlan.Manifest.Metadata.ProviderLockDigest,
                InputDigest: savedPlan.Manifest.Metadata.InputDigest,
                Backend: new IacBackendIdentity(
                    receipt.BackendConfigDigest,
                    receipt.BackendKind,
                    savedPlan.Manifest.Metadata.BackendIsRemote,
                    receipt.Workspace,
                    receipt.ObjectKey,
                    savedPlan.Manifest.Metadata.BackendRegion),
                ExecutionIdentity: new IacExecutionIdentity(
                    receipt.AssumedRoleArn,
                    receipt.RoleId,
                    receipt.AccountId,
                    receipt.Partition ?? savedPlan.Manifest.Metadata.Partition,
                    savedPlan.Manifest.Metadata.Issuer,
                    receipt.CredentialKind),
                State: new IacStateLineage(
                    receipt.StateLineageBefore,
                    receipt.StateSerialBefore,
                    receipt.StateLineageAfter,
                    receipt.StateSerialAfter),
                OperatorContractDigest: operatorContract?.ContractDigest ?? receipt.OutputContractDigest,
                EvidenceMode: savedPlan.Manifest.Metadata.EvidenceMode,
                ReleaseQualified: savedPlan.Manifest.Metadata.ReleaseQualified);

            ProvisioningLineage lineage = new(
                savedPlan.Manifest.ProvisioningOperationId,
                PlanSha256: savedPlan.Manifest.PlanSha256,
                PlanMetadataDigest: receipt.PlanMetadataDigest,
                ApprovalReceiptId: approvalReceipt.ApprovalReceiptId,
                ApprovalReceiptSha256: ComputeSha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(approvalReceipt))),
                ApplyAuditEventId: Guid.NewGuid().ToString("n"),
                // Content-addresses the execution-receipt bytes carried verbatim in the
                // provisioning state and echoed into the binding, so a holder can
                // actually resolve and re-hash what this reference names.
                ActuatorReceiptReference: $"urn:sha256:{receiptSha}");

            SaveProvisioningState(new ProvisioningState(
                lineage,
                savedPlan.Manifest.Stack,
                savedPlan.Manifest.Environment,
                action,
                DateTimeOffset.UtcNow,
                TeardownHandle: TeardownHandle.FromReceipt(receipt, savedPlan.Manifest.Metadata.BackendRegion),
                Execution: execution,
                ExecReceiptSha256: receiptSha,
                ExecReceipt: JsonSerializer.Deserialize<JsonElement>(receiptDocument),
                Endpoint: operatorContract?.Endpoint,
                AdminKeySecretRef: operatorContract?.AdminKeySecretRef,
                OperatorContractDigest: operatorContract?.ContractDigest,
                OperatorContractStatus: operatorContract?.Status));

            return new OperationResponse(
                Status: action == "destroy" ? "infrastructure-destroyed" : "infrastructure-provisioned",
                Summary: action == "destroy"
                    ? $"Break-glass destroy completed for `{savedPlan.Manifest.Stack}` in `{savedPlan.Manifest.Environment}` using reviewed plan `{savedPlan.Manifest.Token}`: {savedPlan.Manifest.PlanSummary}"
                    : $"Infrastructure apply completed for `{savedPlan.Manifest.Stack}` size `{savedPlan.Manifest.Size}` in `{savedPlan.Manifest.Environment}` using reviewed plan `{savedPlan.Manifest.Token}`: {savedPlan.Manifest.PlanSummary}",
                Findings:
                [
                    "The exact approved saved plan was consumed by `scripts/terraform-exact-apply.sh`; no plan was regenerated after approval.",
                    $"Trusted issuer `{approvalReceipt.Issuer}` approved receipt `{approvalReceipt.ApprovalReceiptId}` for plan_metadata_digest {receipt.PlanMetadataDigest}.",
                    ApprovalSigningModes.IsEvidentiary(approvalReceipt.SigningMode)
                        ? $"Approval signing mode `{approvalReceipt.SigningMode}`: the verifying principal holds kms:VerifyMac only and could not have produced this receipt, so it is admissible as approval evidence."
                        : $"NON-EVIDENTIARY approval: signing mode `{approvalReceipt.SigningMode}` keeps the symmetric key in this verifier, so this receipt could have been produced by the party that accepted it. Do not cite it as approval evidence; certification requires `{ApprovalSigningModes.KmsMac}`.",
                    "The substrate re-derived the backend, account, role, inputs, source and state before the mutation and did not refuse.",
                    $"Backend {receipt.BackendKind} workspace `{receipt.Workspace}` object key {receipt.ObjectKey ?? "(none)"} backend_config_digest {receipt.BackendConfigDigest}.",
                    $"State moved from lineage {receipt.StateLineageBefore} serial {receipt.StateSerialBefore} to lineage {receipt.StateLineageAfter} serial {receipt.StateSerialAfter}.",
                    $"Execution identity: {receipt.AssumedRoleArn} in account {receipt.AccountId} ({receipt.CredentialKind}).",
                    $"Execution receipt: urn:sha256:{receiptSha}.",
                    operatorContract is null
                        ? "No operator contract was read for this action."
                        : $"Operator contract {operatorContract.Status}, digest {operatorContract.ContractDigest}, endpoint {operatorContract.Endpoint}.",
                    action == "destroy"
                        ? "Destroy ran only at break-glass tier after its exact elicitation challenge."
                        : "Apply ran only in a non-production environment after its exact elicitation challenge."
                ],
                Actions: action == "destroy"
                    ? ["Complete the required break-glass post-action review and retain the audit operation id."]
                    :
                    [
                        "Run readiness and smoke checks before install_handoff.",
                        $"Call install_handoff with provisioningOperationId={savedPlan.Manifest.ProvisioningOperationId}; the endpoint and admin-key secret reference come from the operator contract and need not be supplied.",
                        "Retain the state lineage/serial, backend config digest and execution role identity as deployment evidence."
                    ],
                ValidationChecks:
                [
                    "reviewed saved plan was unexpired and hash-valid",
                    "saved-plan token was atomically claimed exactly once",
                    "the exact-plan substrate accepted the approval digest and executed the saved plan",
                    "execution receipt validated against terraform-exec-receipt.v1.schema.json",
                    operatorContract is null
                        ? "operator contract not applicable to this action"
                        : "operator contract validated against operator-contract.v1.schema.json"
                ],
                Risks:
                [
                    "Cloud readiness is not implied by a successful Terraform apply; run the service smoke contract.",
                    savedPlan.Manifest.Metadata.ReleaseQualified
                        ? "Losing or exposing Terraform state can prevent safe reconciliation and disclose sensitive metadata."
                        : "This execution was not release-qualified and must not be presented as release evidence."
                ],
                BackendSteps: steps,
                ProvisioningLineage: lineage);
        }
        finally
        {
            DeletePlanDirectory(savedPlan.Directory);
        }
    }

    /// <summary>
    /// Reads the <c>honua.operator-contract/v1</c> projection from the applied stack.
    /// </summary>
    /// <remarks>
    /// <c>terraform output</c> is a read-only projection of state and has no wrapper
    /// in the substrate, so it is invoked directly. Nothing is scraped: the three
    /// structured outputs are parsed and validated against honua-iac's own schema.
    /// </remarks>
    private async Task<(OperatorContract? Contract, OperationResponse? Refusal)> ReadOperatorContractAsync(
        IProvisioningProcessRunner runner,
        TerraformExactSubstrate substrate,
        string terraformRoot,
        List<OperationBackendStep> steps,
        CancellationToken cancellationToken)
    {
        ProvisioningProcessResult outputResult = await runner.RunAsync(
            "terraform",
            ["output", "-json"],
            terraformRoot,
            TimeSpan.FromMinutes(2),
            environment: null,
            cancellationToken);
        steps.Add(ToBackendStep("terraform-output", "operator-contract", outputResult, mutatesState: false));
        if (!outputResult.Succeeded)
        {
            return (null, TerraformFailure(
                "operator-contract-unavailable",
                "The stack outputs could not be read, so no operator contract could be consumed.",
                outputResult,
                steps));
        }

        string schemaJson;
        try
        {
            schemaJson = await File.ReadAllTextAsync(substrate.OperatorContractSchemaPath, cancellationToken);
        }
        catch (IOException exception)
        {
            return (null, ProvisioningRefusal(
                "operator-contract-schema-missing",
                Redaction.Scrub(exception.Message),
                ["Use a honua-iac checkout that ships `infrastructure/terraform/contracts/operator-contract.v1.schema.json`."],
                ["An unvalidated contract is not a contract."]));
        }

        if (!OperatorContract.TryRead(outputResult.StandardOutput, schemaJson, out OperatorContract? contract, out string contractError))
        {
            return (null, ProvisioningRefusal(
                "operator-contract-invalid",
                Redaction.Scrub(contractError),
                ["Update honua-iac so the stack projects a valid honua.operator-contract/v1."],
                ["The endpoint and admin-key locator must come from the stack, not from a caller."]));
        }

        // The contract itself states that certified consumers must reject an
        // unqualified contract, so this is a gate rather than an annotation.
        if (!contract!.IsQualified)
        {
            return (null, ProvisioningRefusal(
                "operator-contract-unqualified",
                $"The stack reports operator contract status `{contract.Status}`; it is missing immutable pins required for release use.",
                ["Deploy a digest-pinned image and supply the release candidate/manifest identity, then re-apply."],
                ["An unqualified contract describes a disposable development plan and cannot back a release claim."]));
        }

        return (contract, null);
    }

    private static string CreatePlanDirectory(out string token)
    {
        token = Guid.NewGuid().ToString("n");
        string root = GetSavedPlanRoot();
        Directory.CreateDirectory(root);
        ProtectDirectory(root);
        string directory = Path.Combine(root, token);
        Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        return directory;
    }

    private static string GetSavedPlanRoot()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "honua-devops-terraform-plans"));

    private static string GetProvisioningStateRoot()
        => Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "honua-devops",
            "provisioning"));

    private static string GetProvisioningStatePath(string provisioningOperationId)
        => Path.Combine(
            GetProvisioningStateRoot(),
            ComputeSha256(Encoding.UTF8.GetBytes(provisioningOperationId)) + ".json");

    private static void SaveProvisioningState(ProvisioningState state)
    {
        string root = GetProvisioningStateRoot();
        Directory.CreateDirectory(root);
        ProtectDirectory(root);
        string destination = GetProvisioningStatePath(state.Lineage.ProvisioningOperationId);
        string temporary = destination + $".{Guid.NewGuid():n}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        ProtectSavedPlan(temporary);
        File.Move(temporary, destination, overwrite: true);
        ProtectSavedPlan(destination);
    }

    private static bool TryLoadProvisioningState(string provisioningOperationId, out ProvisioningState? state)
    {
        state = null;
        try
        {
            string path = GetProvisioningStatePath(provisioningOperationId);
            if (!File.Exists(path)) return false;
            state = JsonSerializer.Deserialize<ProvisioningState>(File.ReadAllText(path));
            return state is not null
                && string.Equals(state.Lineage.ProvisioningOperationId, provisioningOperationId, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void SavePlanManifest(string directory, SavedTerraformPlanManifest manifest)
    {
        string path = Path.Combine(directory, "manifest.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        ProtectSavedPlan(path);
    }

    private bool TryLoadSavedPlan(
        string confirmation,
        string action,
        string stack,
        string size,
        string environment,
        string terraformRoot,
        string approvalReceiptJson,
        out SavedTerraformPlan? savedPlan,
        out ProvisionApprovalReceipt? approvalReceipt,
        out string error)
    {
        savedPlan = null;
        approvalReceipt = null;
        string prefix = $"{action}:{stack}:{environment}:";
        error = $"A reviewed saved plan is required. Expected confirmation prefix `{prefix}` followed by the token returned by the planning call.";
        string normalized = confirmation.Trim();
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string token = normalized[prefix.Length..];
        if (!IsPlanToken(token))
        {
            error = "The saved-plan token is malformed.";
            return false;
        }

        string directory = Path.Combine(GetSavedPlanRoot(), token);
        string manifestPath = Path.Combine(directory, "manifest.json");
        string planFile = Path.Combine(directory, "honua.tfplan");
        if (!File.Exists(manifestPath) || !File.Exists(planFile))
        {
            error = "The saved plan does not exist or has already been consumed.";
            return false;
        }

        SavedTerraformPlanManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SavedTerraformPlanManifest>(File.ReadAllText(manifestPath));
        }
        catch (JsonException)
        {
            error = "The saved-plan manifest is invalid.";
            return false;
        }

        if (manifest is null
            || !string.Equals(manifest.Token, token, StringComparison.Ordinal)
            || !string.Equals(manifest.Stack, stack, StringComparison.Ordinal)
            || !string.Equals(manifest.Size, size, StringComparison.Ordinal)
            || !string.Equals(manifest.Environment, environment, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(manifest.TerraformRoot), Path.GetFullPath(terraformRoot), StringComparison.OrdinalIgnoreCase)
            || manifest.DestroyPlan != (action == "destroy"))
        {
            error = "The saved plan does not match the requested stack, size, environment, root, or action.";
            return false;
        }

        TimeSpan age = DateTimeOffset.UtcNow - manifest.CreatedAtUtc;
        if (age < TimeSpan.FromMinutes(-5) || age > TimeSpan.FromMinutes(30))
        {
            DeletePlanDirectory(directory);
            error = "The saved plan expired; create and review a new plan.";
            return false;
        }

        string actualHash;
        try
        {
            actualHash = ComputeSha256(planFile);
        }
        catch (IOException)
        {
            error = "The saved plan could not be read for integrity verification.";
            return false;
        }
        if (!string.Equals(actualHash, manifest.PlanSha256, StringComparison.OrdinalIgnoreCase))
        {
            DeletePlanDirectory(directory);
            error = "The saved plan failed integrity verification and was deleted.";
            return false;
        }

        if (!TryValidateApprovalReceipt(
                approvalReceiptJson,
                manifest,
                action,
                out approvalReceipt,
                out error))
        {
            return false;
        }

        string claimPath = Path.Combine(directory, "apply.claim");
        try
        {
            using FileStream claim = new(
                claimPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            ProtectSavedPlan(claimPath);
        }
        catch (IOException)
        {
            error = "The saved plan has already been claimed for apply or is currently being consumed. Create and review a new plan.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "The saved plan could not be claimed safely; apply is blocked.";
            return false;
        }

        savedPlan = new SavedTerraformPlan(manifest, planFile, directory);
        return true;
    }

    private bool TryValidateApprovalReceipt(
        string receiptJson,
        SavedTerraformPlanManifest manifest,
        string action,
        out ProvisionApprovalReceipt? receipt,
        out string error)
    {
        receipt = null;
        error = "A signed honua.devops.provision-approval/v1 receipt from a trusted issuer is required.";
        if (string.IsNullOrWhiteSpace(receiptJson))
        {
            return false;
        }
        // Check the receipt against the published schema before anything else, the same
        // way every other contract document in this agent is validated. The hand-rolled
        // checks below still run; this closes the gap where a receipt could satisfy them
        // while violating the contract honua-release issues against.
        IReadOnlyList<string> schemaFindings = ProvisioningContracts.ValidateProvisionApproval(receiptJson);
        if (schemaFindings.Count > 0)
        {
            error = $"The provision approval receipt does not satisfy honua.devops.provision-approval/v1: {string.Join("; ", schemaFindings)}";
            return false;
        }
        try
        {
            receipt = JsonSerializer.Deserialize<ProvisionApprovalReceipt>(receiptJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            error = "The provision approval receipt is malformed.";
            return false;
        }
        if (receipt is null
            || receipt.SchemaVersion != "honua.devops.provision-approval/v1"
            || string.IsNullOrWhiteSpace(receipt.ApprovalReceiptId)
            || receipt.ApprovalReceiptId.Length > 200
            || receipt.Decision != "approved"
            || receipt.ProvisioningOperationId != manifest.ProvisioningOperationId
            || !string.Equals(receipt.PlanSha256, manifest.PlanSha256, StringComparison.OrdinalIgnoreCase)
            // The substrate gates on the plan metadata digest, so the approval must
            // bind it. Approving only the .tfplan hash would leave the backend,
            // account, role, inputs and prior state unapproved.
            || !string.Equals(receipt.PlanMetadataDigest, manifest.PlanMetadataDigest, StringComparison.OrdinalIgnoreCase)
            || receipt.Action != action
            || receipt.Stack != manifest.Stack
            || receipt.Environment != manifest.Environment)
        {
            error = "The approval receipt does not approve this exact operation, plan metadata digest, plan, action, stack, and environment.";
            return false;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (receipt.IssuedAtUtc > now.AddMinutes(5) || receipt.ExpiresAtUtc <= now || receipt.ExpiresAtUtc > receipt.IssuedAtUtc.AddHours(1))
        {
            error = "The approval receipt is expired, not yet valid, or exceeds the one-hour validity ceiling.";
            return false;
        }
        // The signing mode has to be declared, known, and the one this verifier is
        // configured for. Accepting a receipt whose mode we did not expect would let an
        // issuer choose the weaker primitive on the verifier's behalf.
        IApprovalSignatureProvider signatureProvider = ApprovalSignatureProvider;
        if (string.IsNullOrWhiteSpace(receipt.SigningMode) || !ApprovalSigningModes.IsKnown(receipt.SigningMode))
        {
            error = "The approval receipt does not declare a known signing mode.";
            return false;
        }
        if (!string.Equals(receipt.SigningMode, signatureProvider.SigningMode, StringComparison.Ordinal))
        {
            error = $"The approval receipt was signed in `{receipt.SigningMode}` mode but this verifier is configured for `{signatureProvider.SigningMode}`.";
            return false;
        }
        string? expectedKeyId = signatureProvider.ResolveKeyId(receipt.Issuer);
        if (expectedKeyId is null)
        {
            error = $"Approval issuer `{receipt.Issuer}` is not in the configured trusted-issuer allowlist.";
            return false;
        }
        if (!string.Equals(receipt.KeyId, expectedKeyId, StringComparison.Ordinal))
        {
            error = "The approval receipt key id does not match the trusted issuer key.";
            return false;
        }
        string canonical = CanonicalApprovalPayload(receipt);

        // Sync-over-async at exactly one boundary. This host has no SynchronizationContext
        // (console + MCP stdio), so there is no deadlock hazard, and this runs once per
        // apply immediately before a multi-minute terraform run. The provider stays async
        // because the KMS adapter is.
        ApprovalVerificationResult verification = signatureProvider
            .VerifyAsync(receipt.Issuer, canonical, receipt.Signature)
            .GetAwaiter()
            .GetResult();
        if (!verification.Verified)
        {
            error = verification.Detail;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The exact bytes an issuer signs. Every field that changes what the receipt
    /// authorizes is covered, including the signing mode itself.
    /// </summary>
    private static string CanonicalApprovalPayload(ProvisionApprovalReceipt receipt)
        => ApprovalReceiptCanonicalization.Payload(
            receipt.SchemaVersion,
            receipt.ApprovalReceiptId,
            receipt.Issuer,
            receipt.KeyId,
            receipt.ProvisioningOperationId,
            receipt.PlanSha256,
            receipt.PlanMetadataDigest,
            receipt.Action,
            receipt.Stack,
            receipt.Environment,
            receipt.Decision,
            receipt.IssuedAtUtc,
            receipt.ExpiresAtUtc,
            receipt.SigningMode);

    private static void CleanupExpiredSavedPlans()
    {
        string root = GetSavedPlanRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string token = Path.GetFileName(directory);
            if (!IsPlanToken(token))
            {
                continue;
            }

            try
            {
                if (DateTimeOffset.UtcNow - Directory.GetCreationTimeUtc(directory) > TimeSpan.FromHours(1))
                {
                    DeletePlanDirectory(directory);
                }
            }
            catch (IOException)
            {
                // Another process may be consuming the saved plan.
            }
            catch (UnauthorizedAccessException)
            {
                // Fail closed later if this plan token is requested.
            }
        }
    }

    private static void DeletePlanDirectory(string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(directory);
            string rootPrefix = GetSavedPlanRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                && IsPlanToken(Path.GetFileName(fullPath)))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the one-hour expiry sweep retries later.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the one-hour expiry sweep retries later.
        }
    }

    private static bool IsPlanToken(string token)
        => token.Length == 32 && token.All(character => char.IsAsciiHexDigit(character));

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ProtectDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ProtectSavedPlan(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record SavedTerraformPlanManifest(
        string Token,
        string ProvisioningOperationId,
        string Stack,
        string Size,
        string Environment,
        string TerraformRoot,
        DateTimeOffset CreatedAtUtc,
        bool DestroyPlan,
        string PlanSummary,
        string PlanSha256,
        string TerraformRootName,
        string PlanMetadataFile,
        /// <summary>
        /// The substrate's own plan-binding document, carried whole so the apply path
        /// re-reads exactly what the plan path validated rather than a re-derivation
        /// of it.
        /// </summary>
        ExactPlanMetadata Metadata)
    {
        internal string PlanMetadataDigest => Metadata.PlanMetadataDigest;
    }

    private sealed record ProvisionApprovalReceipt(
        string SchemaVersion,
        string ApprovalReceiptId,
        string Issuer,
        string KeyId,
        string ProvisioningOperationId,
        string PlanSha256,
        /// <summary>
        /// The honua-iac exact-plan metadata digest this approval authorizes. It is
        /// what <c>terraform-exact-apply.sh</c> checks, so an approval that omitted it
        /// could not gate the mutation the substrate actually performs.
        /// </summary>
        string PlanMetadataDigest,
        string Action,
        string Stack,
        string Environment,
        string Decision,
        /// <summary>
        /// How the signature was produced (honua-devops#175). It is covered by the
        /// signature itself, so an attacker cannot downgrade a <c>kms-mac</c> receipt to
        /// <c>local-hmac-dev</c> without invalidating it. A receipt that omits this field
        /// is refused: the verifier will not guess how a signature was made.
        /// </summary>
        string SigningMode,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        string Signature);

    private sealed record SavedTerraformPlan(
        SavedTerraformPlanManifest Manifest,
        string PlanFile,
        string Directory);

    private sealed record ProvisioningState(
        ProvisioningLineage Lineage,
        string Stack,
        string Environment,
        string Action,
        DateTimeOffset AppliedAtUtc,
        TeardownHandle TeardownHandle,
        IacExecutionEvidence? Execution = null,
        string? ExecReceiptSha256 = null,
        JsonElement ExecReceipt = default,
        string? Endpoint = null,
        string? AdminKeySecretRef = null,
        string? OperatorContractDigest = null,
        string? OperatorContractStatus = null);

    private static OperationBackendStep ToBackendStep(
        string name,
        string stack,
        ProvisioningProcessResult result,
        bool mutatesState)
    {
        return new OperationBackendStep(
            name,
            $"terraform://{stack}/{name}",
            result.Succeeded,
            result.TimedOut ? "process timed out" : $"process exited {result.ExitCode}",
            result.Succeeded ? ReadPlanSummary(result.StandardOutput) : BuildDiagnostic(result),
            mutatesState);
    }

    // A bounded, redacted projection of `terraform show` over the saved plan: the per-resource
    // change roster plus an explicit destructive-change list, so the confirmation challenge is
    // never the first time a caller learns something is being replaced or destroyed.
    private sealed record TerraformPlanReview(
        IReadOnlyList<string> ResourceChanges,
        IReadOnlyList<string> DestructiveChanges,
        int ChangeCount,
        bool Truncated,
        string ReviewDigest);

    private const int MaxReviewedResourceChanges = 200;

    private static TerraformPlanReview BuildPlanReview(ProvisioningProcessResult showResult)
    {
        if (!showResult.Succeeded)
        {
            return new TerraformPlanReview(
                ["Plan review unavailable: `terraform show` did not succeed for the saved plan."],
                [],
                0,
                false,
                "unavailable");
        }

        string output = Redaction.Scrub(showResult.StandardOutput ?? string.Empty);
        List<string> changes = [];
        List<string> destructive = [];

        foreach (string rawLine in output.Split('\n'))
        {
            Match match = PlanResourceChangePattern.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            string address = match.Groups["address"].Value.Trim();
            string verb = match.Groups["verb"].Value.Trim();
            string entry = $"  {verb}: {address}";
            changes.Add(entry);

            if (verb.Contains("destroy", StringComparison.OrdinalIgnoreCase)
                || verb.Contains("replace", StringComparison.OrdinalIgnoreCase))
            {
                destructive.Add($"{verb} {address}");
            }
        }

        bool truncated = changes.Count > MaxReviewedResourceChanges;
        if (truncated)
        {
            changes = [.. changes.Take(MaxReviewedResourceChanges)];
            changes.Add($"  ...({MaxReviewedResourceChanges}+ changes; review the full plan in the operator session)");
        }

        if (changes.Count == 0)
        {
            changes.Add("  (no resource-level changes were parsed from the plan output)");
        }

        return new TerraformPlanReview(
            changes,
            destructive,
            truncated ? MaxReviewedResourceChanges : changes.Count,
            truncated,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output))));
    }

    private static string ReadPlanSummary(string output)
    {
        Match match = PlanSummaryPattern.Match(output ?? string.Empty);
        return match.Success
            ? $"{match.Groups["add"].Value} to add, {match.Groups["change"].Value} to change, {match.Groups["destroy"].Value} to destroy"
            : "plan summary unavailable (review the operator-local Terraform output)";
    }

    private static string BuildDiagnostic(ProvisioningProcessResult result)
    {
        string diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        string scrubbed = Redaction.Scrub(diagnostic).ReplaceLineEndings(" ").Trim();
        return scrubbed.Length <= 2048 ? scrubbed : scrubbed[..2048] + "<truncated>";
    }

    private static OperationResponse TerraformFailure(
        string status,
        string summary,
        ProvisioningProcessResult result,
        IReadOnlyList<OperationBackendStep> steps)
    {
        return new OperationResponse(
            Status: status,
            Summary: summary,
            Findings: [BuildDiagnostic(result)],
            Actions:
            [
                "Correct the local honua-iac, Terraform, credentials, variables, or remote-state configuration, then request a new plan.",
                "Do not bypass the saved-plan gate or move secret values into variablesJson."
            ],
            ValidationChecks: ["failed process produced a bounded redacted diagnostic"],
            Risks: ["A failed apply can leave partially-created resources; inspect Terraform state before retrying."],
            BackendSteps: steps);
    }

    private static OperationResponse ProvisioningRefusal(
        string status,
        string summary,
        IReadOnlyList<string> actions,
        IReadOnlyList<string> risks)
    {
        return new OperationResponse(
            Status: status,
            Summary: summary,
            Findings: [],
            Actions: actions,
            ValidationChecks: ["no Terraform process started"],
            Risks: risks);
    }

    private static string NormalizeToken(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static bool TryValidateBaseUrl(string value, out Uri? uri, out string error)
    {
        uri = null;
        error = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "baseUrl must be an absolute URL without credentials, query, or fragment.";
            return false;
        }

        bool loopbackHttp = parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
        if (parsed.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
        {
            error = "baseUrl must use HTTPS; HTTP is allowed only for loopback development endpoints.";
            return false;
        }

        uri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private static bool IsSecretReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 1024
            || value.Any(char.IsWhiteSpace)
            || value.Any(char.IsControl))
        {
            return false;
        }

        return value.StartsWith("arn:aws:secretsmanager:", StringComparison.Ordinal)
            || value.StartsWith("aws-secretsmanager://", StringComparison.Ordinal)
            || value.StartsWith("azure-key-vault://", StringComparison.Ordinal)
            || value.StartsWith("https://", StringComparison.Ordinal)
                && value.Contains(".vault.azure.net/secrets/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("secret://", StringComparison.Ordinal);
    }

    private static string ResolveHandoffDirectory(string configured, string stack)
    {
        string directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.CurrentDirectory, ".honua", "handoffs", stack)
            : configured.Trim();
        string fullPath = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The handoff directory cannot be a filesystem root.");
        }
        return fullPath;
    }
}
