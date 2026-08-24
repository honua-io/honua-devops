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

    [Description("Plan, apply, or break-glass destroy an allowlisted honua-iac stack with strong argv invocation and plan-before-mutation gates.")]
    public async Task<OperationResponse> ProvisionInfrastructureAsync(
        string stack,
        string size,
        string action,
        string variablesJson,
        bool confirmed,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        const string canonicalStack = "aws-ecs";
        const string canonicalSize = "small";
        string normalizedStack = NormalizeToken(stack);
        string normalizedSize = NormalizeToken(size);
        string normalizedAction = NormalizeToken(action);

        if (!string.Equals(normalizedStack, canonicalStack, StringComparison.Ordinal))
        {
            return ProvisioningRefusal(
                "unsupported-stack",
                $"Stack `{normalizedStack}` is not available. 2026.1 allows only `{canonicalStack}` (honua-iac `examples/aws`).",
                ["Choose stack=aws-ecs. AWS serverless and Helm remain sequenced follow-ups."],
                ["Never resolve a caller-controlled stack directly to a filesystem path."]);
        }

        if (!string.Equals(normalizedSize, canonicalSize, StringComparison.Ordinal))
        {
            return ProvisioningRefusal(
                "unsupported-size",
                $"Size `{normalizedSize}` is not available. 2026.1 allows only `{canonicalSize}`.",
                ["Choose size=small or plan a reviewed custom topology outside this tool."],
                ["Unreviewed size values could bypass the single-node development contract."]);
        }

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

        string terraformRoot;
        try
        {
            terraformRoot = ResolveStackRoot(runtime.TerraformLocalPath, "aws");
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
                        out SavedTerraformPlan? savedPlan,
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

                return await ApplySavedPlanAsync(savedPlan!, normalizedAction, cancellationToken);
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
        string variableFile = Path.Combine(planDirectory, "small.auto.tfvars.json");
        string planFile = Path.Combine(planDirectory, "honua.tfplan");
        bool keepPlanDirectory = false;
        try
        {
            await File.WriteAllTextAsync(
                variableFile,
                JsonSerializer.Serialize(variables, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            IProvisioningProcessRunner runner = provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance;
            List<OperationBackendStep> steps = [];
            ProvisioningProcessResult initResult = await runner.RunAsync(
                "terraform",
                ["init", "-input=false", "-no-color"],
                terraformRoot,
                TimeSpan.FromMinutes(5),
                cancellationToken);
            steps.Add(ToBackendStep("terraform-init", canonicalStack, initResult, mutatesState: false));
            if (!initResult.Succeeded)
            {
                return TerraformFailure("terraform-init-failed", "Terraform init failed.", initResult, steps);
            }

            List<string> planArguments =
            [
                "plan",
                "-input=false",
                "-no-color",
                "-lock-timeout=60s",
                $"-var-file={variableFile}"
            ];
            if (normalizedAction == "destroy")
            {
                planArguments.Add("-destroy");
            }
            planArguments.Add($"-out={planFile}");

            ProvisioningProcessResult planResult = await runner.RunAsync(
                "terraform",
                planArguments,
                terraformRoot,
                TimeSpan.FromMinutes(15),
                cancellationToken);
            steps.Add(ToBackendStep("terraform-plan", canonicalStack, planResult, mutatesState: false));
            if (!planResult.Succeeded)
            {
                return TerraformFailure("terraform-plan-failed", "Terraform plan failed; apply was not attempted.", planResult, steps);
            }

            string planSummary = ReadPlanSummary(planResult.StandardOutput);
            if (!File.Exists(planFile))
            {
                return TerraformFailure(
                    "terraform-plan-artifact-missing",
                    "Terraform reported success but did not create the required saved-plan artifact; apply is blocked.",
                    new ProvisioningProcessResult(1, string.Empty, "saved plan artifact missing", false),
                    steps);
            }

            // Reviewable evidence. `terraform show` against the SAVED PLAN is read-only and
            // is the only way an MCP caller can see which resources, replacements, and
            // deletions it is being asked to confirm. Without it the response tells the
            // caller to "review the complete plan" while handing it only three numbers.
            ProvisioningProcessResult showResult = await runner.RunAsync(
                "terraform",
                ["show", "-no-color", planFile],
                terraformRoot,
                TimeSpan.FromMinutes(5),
                cancellationToken);
            steps.Add(ToBackendStep("terraform-show", canonicalStack, showResult, mutatesState: false));
            TerraformPlanReview planReview = BuildPlanReview(showResult);

            bool destroyPlan = normalizedAction == "destroy";
            ProtectSavedPlan(planFile);
            SavePlanManifest(
                planDirectory,
                new SavedTerraformPlanManifest(
                    planToken,
                    canonicalStack,
                    canonicalSize,
                    environment,
                    terraformRoot,
                    DateTimeOffset.UtcNow,
                    destroyPlan,
                    planSummary,
                    ComputeSha256(planFile)));
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
                    $"Saved plan token: {planToken} (expires in 30 minutes).",
                    "Invocation used direct argv; no shell command string was evaluated.",
                    "Generated variables were restricted to the non-secret aws-ecs/small allowlist.",
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
                    $"Repeat with action={nextAction}, confirmed=true, and confirmation={challenge}. The saved plan, not a newly generated plan, will be applied.",
                    "Configure and protect a remote state backend before creating a shared or long-lived cell."
                ],
                ValidationChecks:
                [
                    "terraform init succeeded",
                    destroyPlan ? "terraform destroy plan succeeded" : "terraform plan succeeded",
                    "saved-plan artifact exists and is token-bound",
                    "no apply process started"
                ],
                Risks:
                [
                    "The saved plan expires after 30 minutes and Terraform will reject it if state has changed.",
                    "Saved Terraform plans can contain sensitive values; this artifact is kept in the current user's protected temporary directory and deleted after use or expiry.",
                    "Local Terraform state is unsuitable for a shared or long-lived environment."
                ],
                BackendSteps: steps);
        }
        finally
        {
            if (!keepPlanDirectory)
            {
                DeletePlanDirectory(planDirectory);
            }
        }
    }

    [Description("Write a secretless Honua CLI/MCP handoff using a Terraform honua_url and a secret-store reference.")]
    public async Task<OperationResponse> InstallHandoffAsync(
        string stack,
        string baseUrl,
        string adminKeySecretRef,
        string outputDirectory,
        bool overwrite,
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

        string terraformRoot;
        try
        {
            terraformRoot = ResolveStackRoot(runtime.TerraformLocalPath, "aws");
        }
        catch (InvalidOperationException exception)
        {
            return ProvisioningRefusal(
                "terraform-root-invalid",
                Redaction.Scrub(exception.Message),
                ["Set HONUA_DEVOPS_TERRAFORM_LOCAL_PATH to a current honua-iac checkout."],
                []);
        }

        string normalizedBaseUrl = baseUrl.Trim();
        List<OperationBackendStep> steps = [];
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            IProvisioningProcessRunner runner = provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance;
            ProvisioningProcessResult outputResult = await runner.RunAsync(
                "terraform",
                ["output", "-json"],
                terraformRoot,
                TimeSpan.FromMinutes(2),
                cancellationToken);
            steps.Add(ToBackendStep("terraform-output", canonicalStack, outputResult, mutatesState: false));
            if (!outputResult.Succeeded)
            {
                return TerraformFailure(
                    "terraform-output-failed",
                    "Could not read the honua_url Terraform output.",
                    outputResult,
                    steps);
            }

            try
            {
                normalizedBaseUrl = ReadHonuaUrl(outputResult.StandardOutput);
            }
            catch (InvalidOperationException exception)
            {
                return ProvisioningRefusal(
                    "honua-url-missing",
                    exception.Message,
                    ["Pass baseUrl explicitly or expose the honua_url output from the stack."],
                    []);
            }
        }

        if (!TryValidateBaseUrl(normalizedBaseUrl, out Uri? parsedBaseUrl, out string urlError))
        {
            return ProvisioningRefusal(
                "base-url-invalid",
                urlError,
                ["Use an absolute HTTPS URL, or HTTP only for a loopback development endpoint."],
                ["An untrusted proxy endpoint could receive the resolved admin credential at launch time."]);
        }

        string normalizedSecretRef = adminKeySecretRef.Trim();
        if (!IsSecretReference(normalizedSecretRef))
        {
            return ProvisioningRefusal(
                "secret-ref-invalid",
                "adminKeySecretRef must be an AWS Secrets Manager ARN/URI, Azure Key Vault URI, or secret:// reference. Secret material is not accepted.",
                ["Pass the cloud secret identifier, never the admin-key value."],
                ["A literal admin key must not be persisted in the handoff files or audit journal."]);
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
            command = "npx",
            args = new[] { "-y", "--package", "@honua/mcp-server", "honua-mcp-proxy" },
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
        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
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
            Status: "install-handoff-ready",
            Summary: $"Secretless Honua CLI/MCP handoff written for `{canonicalStack}` at `{handoffDirectory}`.",
            Findings:
            [
                $"HONUA_BASE_URL={parsedBaseUrl.AbsoluteUri.TrimEnd('/')}",
                $"HONUA_ADMIN_KEY secret reference={normalizedSecretRef}",
                $"HONUA_MCP_REMOTE_URL={mcpUri.AbsoluteUri}",
                $"Proxy handoff: {configPath}",
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

    private static string ResolveStackRoot(string configuredRoot, string actualRootName)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException("The honua-iac root is not configured.");
        }

        string repositoryRoot = Path.GetFullPath(configuredRoot);
        string examplesRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "infrastructure", "terraform", "examples"));
        string stackRoot = Path.GetFullPath(Path.Combine(examplesRoot, actualRootName));
        string expectedPrefix = examplesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!stackRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(stackRoot, "main.tf"))
            || !File.Exists(Path.Combine(stackRoot, "variables.tf")))
        {
            throw new InvalidOperationException($"The allowlisted deployable root `{actualRootName}` is missing from the configured honua-iac checkout.");
        }
        return stackRoot;
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
        SavedTerraformPlan savedPlan,
        string action,
        CancellationToken cancellationToken)
    {
        IProvisioningProcessRunner runner = provisioningProcessRunner ?? SystemProvisioningProcessRunner.Instance;
        List<OperationBackendStep> steps = [];
        try
        {
            ProvisioningProcessResult applyResult = await runner.RunAsync(
                "terraform",
                ["apply", "-input=false", "-no-color", "-auto-approve", savedPlan.PlanFile],
                savedPlan.Manifest.TerraformRoot,
                TimeSpan.FromMinutes(45),
                cancellationToken);
            steps.Add(ToBackendStep(
                action == "destroy" ? "terraform-destroy-apply" : "terraform-apply",
                savedPlan.Manifest.Stack,
                applyResult,
                mutatesState: true));

            if (!applyResult.Succeeded)
            {
                return TerraformFailure(
                    action == "destroy" ? "terraform-destroy-failed" : "terraform-apply-failed",
                    action == "destroy"
                        ? "The previously reviewed destroy plan failed during apply."
                        : "The previously reviewed Terraform plan failed during apply.",
                    applyResult,
                    steps);
            }

            return new OperationResponse(
                Status: action == "destroy" ? "infrastructure-destroyed" : "infrastructure-provisioned",
                Summary: action == "destroy"
                    ? $"Break-glass destroy completed for `{savedPlan.Manifest.Stack}` in `{savedPlan.Manifest.Environment}` using reviewed plan `{savedPlan.Manifest.Token}`: {savedPlan.Manifest.PlanSummary}"
                    : $"Infrastructure apply completed for `{savedPlan.Manifest.Stack}` size `{savedPlan.Manifest.Size}` in `{savedPlan.Manifest.Environment}` using reviewed plan `{savedPlan.Manifest.Token}`: {savedPlan.Manifest.PlanSummary}",
                Findings:
                [
                    "The exact token-bound plan returned by the previous planning call was the only artifact passed to terraform apply.",
                    "The saved plan hash, stack, root, size, environment, action, and age were verified before process start.",
                    "An atomic one-time claim was acquired before process start; concurrent reuse of the token fails closed.",
                    "Invocation used direct argv; no shell command string was evaluated.",
                    action == "destroy"
                        ? "Destroy ran only at break-glass tier after its exact elicitation challenge."
                        : "Apply ran only in a non-production environment after its exact elicitation challenge."
                ],
                Actions: action == "destroy"
                    ? ["Complete the required break-glass post-action review and retain the audit operation id."]
                    :
                    [
                        "Run readiness and smoke checks before install_handoff.",
                        "Call install_handoff with the Terraform honua_url and the cloud secret reference for the admin key.",
                        "Retain the remote state version and both planning/apply audit operation ids as deployment evidence."
                    ],
                ValidationChecks:
                [
                    "reviewed saved plan was unexpired and hash-valid",
                    "saved-plan token was atomically claimed exactly once",
                    "terraform apply of the exact saved plan succeeded",
                    "saved plan was deleted after the one-time apply attempt"
                ],
                Risks:
                [
                    "Cloud readiness is not implied by a successful Terraform apply; run the service smoke contract.",
                    "Losing or exposing Terraform state can prevent safe reconciliation and disclose sensitive metadata."
                ],
                BackendSteps: steps);
        }
        finally
        {
            DeletePlanDirectory(savedPlan.Directory);
        }
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

    private static void SavePlanManifest(string directory, SavedTerraformPlanManifest manifest)
    {
        string path = Path.Combine(directory, "manifest.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        ProtectSavedPlan(path);
    }

    private static bool TryLoadSavedPlan(
        string confirmation,
        string action,
        string stack,
        string size,
        string environment,
        string terraformRoot,
        out SavedTerraformPlan? savedPlan,
        out string error)
    {
        savedPlan = null;
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
        string Stack,
        string Size,
        string Environment,
        string TerraformRoot,
        DateTimeOffset CreatedAtUtc,
        bool DestroyPlan,
        string PlanSummary,
        string PlanSha256);

    private sealed record SavedTerraformPlan(
        SavedTerraformPlanManifest Manifest,
        string PlanFile,
        string Directory);

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

    private static string ReadHonuaUrl(string terraformOutput)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(terraformOutput);
            if (document.RootElement.TryGetProperty("honua_url", out JsonElement output)
                && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
            // Converted to the stable refusal below.
        }

        throw new InvalidOperationException("Terraform output did not contain a string `honua_url.value`.");
    }

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
