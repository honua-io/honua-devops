using YamlDotNet.RepresentationModel;

namespace Honua.DevOps.Agent.Operations.DesiredState;

// Parses the desired-state/ object tree, validates each object against its
// schema and the shared conventions, checks the operator control-model policy
// rules, and emits a typed remediation plan. This is intentionally read-only:
// it never mutates objects, never applies state, and never bypasses an
// execution/approval gate -- it only reports what an operator would need to fix.
internal sealed class DesiredStateDriftDetector
{
    private const string ExpectedApiVersion = DesiredStateApi.ApiVersion;

    private readonly DesiredStateConventions _conventions;

    internal DesiredStateDriftDetector(DesiredStateConventions conventions)
    {
        _conventions = conventions;
    }

    // Loads conventions.env and every *.yaml object under the given root.
    internal static DesiredStateDriftReport DetectFromDirectory(string desiredStateRoot)
    {
        if (!Directory.Exists(desiredStateRoot))
        {
            throw new DirectoryNotFoundException($"Desired-state root not found: `{desiredStateRoot}`.");
        }

        string conventionsPath = Path.Combine(desiredStateRoot, "conventions.env");
        if (!File.Exists(conventionsPath))
        {
            throw new FileNotFoundException($"Desired-state conventions file not found: `{conventionsPath}`.");
        }

        DesiredStateConventions conventions = DesiredStateConventions.Parse(File.ReadAllText(conventionsPath));

        string[] yamlFiles = Directory
            .GetFiles(desiredStateRoot, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        DesiredStateDocument[] documents = yamlFiles
            .Select(path => DesiredStateDocument.Load(path, File.ReadAllText(path)))
            .ToArray();

        return new DesiredStateDriftDetector(conventions).Detect(desiredStateRoot, documents);
    }

    // Validates an already-parsed set of documents. Exposed for unit testing
    // against in-memory fixtures without touching the filesystem.
    internal DesiredStateDriftReport Detect(string desiredStateRoot, IReadOnlyList<DesiredStateDocument> documents)
    {
        Dictionary<DesiredStateObjectKey, DesiredStateDocument> byKey = new();
        foreach (DesiredStateDocument document in documents)
        {
            if (document.ParsedOk && document.Key is { } key)
            {
                byKey.TryAdd(key, document);
            }
        }

        int filesFailed = 0;
        List<ObjectRemediation> remediations = [];

        foreach (DesiredStateDocument document in documents)
        {
            List<DriftIssue> issues = [];

            if (!document.ParsedOk)
            {
                filesFailed++;
                issues.Add(new DriftIssue(
                    DriftIssueType.SchemaMismatch,
                    DriftSeverity.Error,
                    document.ParseError ?? "Object failed to parse.",
                    "Fix the YAML so it parses to a single mapping document with apiVersion, kind, metadata.name, and metadata.namespace.",
                    FieldPath: "<document>"));
                remediations.Add(new ObjectRemediation(document.Path, document.Kind, document.Name, document.Namespace, issues));
                continue;
            }

            ValidateEnvelope(document, issues);

            switch (document.Kind)
            {
                case DesiredStateApi.PlatformStackKind:
                    ValidatePlatformStack(document, issues);
                    break;
                case DesiredStateApi.ExecutionPolicyKind:
                    ValidateExecutionPolicy(document, issues);
                    break;
                case DesiredStateApi.PlatformReleaseKind:
                    ValidatePlatformRelease(document, issues);
                    break;
                case DesiredStateApi.PromotionKind:
                    ValidatePromotion(document, byKey, issues);
                    break;
                case DesiredStateApi.ServiceBundleKind:
                    ValidateServiceBundle(document, byKey, issues);
                    break;
                default:
                    issues.Add(new DriftIssue(
                        DriftIssueType.SchemaMismatch,
                        DriftSeverity.Error,
                        $"Unsupported desired-state kind `{document.Kind ?? "<missing>"}`.",
                        $"Use one of: {DesiredStateApi.PlatformStackKind}, {DesiredStateApi.PlatformReleaseKind}, {DesiredStateApi.ServiceBundleKind}, {DesiredStateApi.PromotionKind}, {DesiredStateApi.ExecutionPolicyKind}.",
                        FieldPath: "kind"));
                    break;
            }

            remediations.Add(new ObjectRemediation(document.Path, document.Kind, document.Name, document.Namespace, issues));
        }

        return new DesiredStateDriftReport(desiredStateRoot, byKey.Count, filesFailed, remediations);
    }

    private static void ValidateEnvelope(DesiredStateDocument document, List<DriftIssue> issues)
    {
        if (document.ApiVersion != ExpectedApiVersion)
        {
            issues.Add(Schema(
                $"apiVersion is `{document.ApiVersion ?? "<missing>"}`, expected `{ExpectedApiVersion}`.",
                $"Set apiVersion to `{ExpectedApiVersion}`.",
                "apiVersion"));
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            issues.Add(Schema("metadata.name is missing.", "Add a metadata.name.", "metadata.name"));
        }

        if (string.IsNullOrWhiteSpace(document.Namespace))
        {
            issues.Add(Schema("metadata.namespace is missing.", "Add a metadata.namespace.", "metadata.namespace"));
        }
    }

    private void ValidatePlatformStack(DesiredStateDocument document, List<DriftIssue> issues)
    {
        string? environment = document.TryScalar("spec", "environment");
        RequireScalar(document, issues, "spec.environment", environment);

        if (environment is not null && document.Namespace is not null && !string.Equals(environment, document.Namespace, StringComparison.Ordinal))
        {
            issues.Add(Schema(
                $"spec.environment `{environment}` must match metadata.namespace `{document.Namespace}`.",
                "Align spec.environment with metadata.namespace (objects are environment-scoped).",
                "spec.environment"));
        }

        if (environment is not null && document.Name is not null)
        {
            string expectedName = $"{_conventions.PlatformStackPrefix}-{environment}";
            if (!string.Equals(document.Name, expectedName, StringComparison.Ordinal))
            {
                issues.Add(Schema(
                    $"PlatformStack name `{document.Name}` does not match convention `{expectedName}`.",
                    $"Rename the object to `{expectedName}`.",
                    "metadata.name"));
            }
        }

        ValidateTerraform(document, issues, "spec", "terraform");

        if (document.TrySequence("spec", "secretRefs") is not { Count: > 0 })
        {
            issues.Add(Schema("spec.secretRefs is missing or empty.", "List the secret reference keys this stack consumes.", "spec.secretRefs"));
        }
    }

    private void ValidateExecutionPolicy(DesiredStateDocument document, List<DriftIssue> issues)
    {
        if (document.Namespace is not null && !string.Equals(document.Namespace, _conventions.ControlPlaneNamespace, StringComparison.Ordinal))
        {
            issues.Add(Schema(
                $"ExecutionPolicy namespace `{document.Namespace}` must be `{_conventions.ControlPlaneNamespace}`.",
                $"Move the ExecutionPolicy into namespace `{_conventions.ControlPlaneNamespace}`.",
                "metadata.namespace"));
        }

        bool isDefault = string.Equals(document.Name, _conventions.ExecutionPolicyDefaultName, StringComparison.Ordinal);
        bool isBreakGlass = string.Equals(document.Name, _conventions.ExecutionPolicyBreakGlassName, StringComparison.Ordinal);
        if (!isDefault && !isBreakGlass)
        {
            issues.Add(Schema(
                $"ExecutionPolicy name `{document.Name}` is not a shared policy name.",
                $"Use `{_conventions.ExecutionPolicyDefaultName}` or `{_conventions.ExecutionPolicyBreakGlassName}`.",
                "metadata.name"));
        }

        string? executionMode = document.TryScalar("spec", "executionMode");
        string? executionTier = document.TryScalar("spec", "executionTier");
        RequireScalar(document, issues, "spec.executionMode", executionMode);
        RequireScalar(document, issues, "spec.executionTier", executionTier);

        if (document.TrySequence("spec", "allowedEnvironments") is not { Count: > 0 })
        {
            issues.Add(Schema("spec.allowedEnvironments is missing or empty.", "List the environments this policy governs.", "spec.allowedEnvironments"));
        }

        if (document.TrySequence("spec", "requiredChecks") is not { Count: > 0 })
        {
            issues.Add(Schema("spec.requiredChecks is missing or empty.", "List the gate checks this policy enforces.", "spec.requiredChecks"));
        }

        bool? requiresApproval = TryBool(document, "spec", "requiresApproval");
        bool? allowsBreakGlass = TryBool(document, "spec", "allowsBreakGlass");

        // Policy-model rules from docs/operator-control-contract.md: the default
        // posture is plan-first / approval-required and must not enable break-glass;
        // break-glass behaviour belongs only to the named break-glass policy.
        if (isDefault)
        {
            if (requiresApproval == false)
            {
                issues.Add(Policy(
                    "Default ExecutionPolicy sets requiresApproval=false; the default posture must require approval.",
                    "Set spec.requiresApproval to true on the default policy.",
                    "spec.requiresApproval"));
            }

            if (allowsBreakGlass == true)
            {
                issues.Add(Policy(
                    "Default ExecutionPolicy sets allowsBreakGlass=true; break-glass must stay on the dedicated break-glass policy.",
                    "Set spec.allowsBreakGlass to false on the default policy.",
                    "spec.allowsBreakGlass"));
            }

            if (executionMode is not null && !string.Equals(executionMode, "plan", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Policy(
                    $"Default ExecutionPolicy executionMode `{executionMode}` should be `plan` for the plan-first default posture.",
                    "Set spec.executionMode to `plan` on the default policy.",
                    "spec.executionMode"));
            }
        }

        if (isBreakGlass && allowsBreakGlass == false)
        {
            issues.Add(Policy(
                "Break-glass ExecutionPolicy sets allowsBreakGlass=false.",
                "Set spec.allowsBreakGlass to true on the break-glass policy, or remove the object.",
                "spec.allowsBreakGlass"));
        }
    }

    private void ValidatePlatformRelease(DesiredStateDocument document, List<DriftIssue> issues)
    {
        string? service = document.TryScalar("spec", "service");
        string? environment = document.TryScalar("spec", "environment");
        string? revision = document.TryScalar("spec", "revision");
        RequireScalar(document, issues, "spec.service", service);
        RequireScalar(document, issues, "spec.environment", environment);
        RequireScalar(document, issues, "spec.revision", revision);
        RequireScalar(document, issues, "spec.action", document.TryScalar("spec", "action"));
        RequireScalar(document, issues, "spec.gitOpsTool", document.TryScalar("spec", "gitOpsTool"));

        if (environment is not null && document.Namespace is not null && !string.Equals(environment, document.Namespace, StringComparison.Ordinal))
        {
            issues.Add(Schema(
                $"spec.environment `{environment}` must match metadata.namespace `{document.Namespace}`.",
                "Align spec.environment with metadata.namespace.",
                "spec.environment"));
        }

        if (service is not null && document.Namespace is not null && revision is not null && document.Name is not null)
        {
            string expected = _conventions.RenderPlatformReleaseName(service, document.Namespace, revision);
            if (!string.Equals(document.Name, expected, StringComparison.Ordinal))
            {
                issues.Add(Schema(
                    $"PlatformRelease name `{document.Name}` does not match convention `{expected}`.",
                    $"Rename the object to `{expected}`.",
                    "metadata.name"));
            }
        }

        ValidateTerraform(document, issues, "spec", "terraform");
    }

    private void ValidatePromotion(
        DesiredStateDocument document,
        IReadOnlyDictionary<DesiredStateObjectKey, DesiredStateDocument> byKey,
        List<DriftIssue> issues)
    {
        string? service = document.TryScalar("spec", "service");
        string? source = document.TryScalar("spec", "sourceEnvironment");
        string? target = document.TryScalar("spec", "targetEnvironment");
        RequireScalar(document, issues, "spec.service", service);
        RequireScalar(document, issues, "spec.sourceEnvironment", source);
        RequireScalar(document, issues, "spec.targetEnvironment", target);
        RequireScalar(document, issues, "spec.revision", document.TryScalar("spec", "revision"));

        if (target is not null && document.Namespace is not null && !string.Equals(target, document.Namespace, StringComparison.Ordinal))
        {
            issues.Add(Schema(
                $"spec.targetEnvironment `{target}` must match metadata.namespace `{document.Namespace}`.",
                "Align spec.targetEnvironment with metadata.namespace.",
                "spec.targetEnvironment"));
        }

        if (service is not null && source is not null && target is not null && document.Name is not null)
        {
            string expected = _conventions.RenderPromotionName(service, source, target);
            if (!string.Equals(document.Name, expected, StringComparison.Ordinal))
            {
                issues.Add(Schema(
                    $"Promotion name `{document.Name}` does not match convention `{expected}`.",
                    $"Rename the object to `{expected}`.",
                    "metadata.name"));
            }
        }

        ValidateExecutionPolicyRef(document, byKey, issues, "spec", "executionPolicyRef");
    }

    private void ValidateServiceBundle(
        DesiredStateDocument document,
        IReadOnlyDictionary<DesiredStateObjectKey, DesiredStateDocument> byKey,
        List<DriftIssue> issues)
    {
        RequireScalar(document, issues, "spec.description", document.TryScalar("spec", "description"));

        string? service = document.TryScalar("spec", "deployment", "service");
        string? environment = document.TryScalar("spec", "deployment", "environment");
        string? revision = document.TryScalar("spec", "deployment", "revision");
        string? action = document.TryScalar("spec", "deployment", "action");
        RequireScalar(document, issues, "spec.deployment.service", service);
        RequireScalar(document, issues, "spec.deployment.environment", environment);
        RequireScalar(document, issues, "spec.deployment.revision", revision);
        RequireScalar(document, issues, "spec.deployment.action", action);
        RequireScalar(document, issues, "spec.deployment.gitOpsTool", document.TryScalar("spec", "deployment", "gitOpsTool"));

        if (environment is not null && document.Namespace is not null && !string.Equals(environment, document.Namespace, StringComparison.Ordinal))
        {
            issues.Add(Schema(
                $"spec.deployment.environment `{environment}` must match metadata.namespace `{document.Namespace}`.",
                "Align spec.deployment.environment with metadata.namespace.",
                "spec.deployment.environment"));
        }

        if (service is not null && document.Namespace is not null && document.Name is not null)
        {
            string expected = _conventions.RenderServiceBundleName(service, document.Namespace);
            if (!string.Equals(document.Name, expected, StringComparison.Ordinal))
            {
                issues.Add(Schema(
                    $"ServiceBundle name `{document.Name}` does not match convention `{expected}`.",
                    $"Rename the object to `{expected}`.",
                    "metadata.name"));
            }
        }

        ValidateTerraform(document, issues, "spec", "deployment", "terraform");

        // Reference resolution.
        ValidateReferenceExists(document, byKey, issues, "spec", "relationships", "platformStackRef");
        ValidateReferenceExists(document, byKey, issues, "spec", "relationships", "platformReleaseRef");
        ValidateExecutionPolicyRef(document, byKey, issues, "spec", "relationships", "executionPolicyRef");

        if (service is not null && document.Namespace is not null && revision is not null)
        {
            string? releaseRefName = document.TryScalar("spec", "relationships", "platformReleaseRef", "name");
            string expectedRelease = _conventions.RenderPlatformReleaseName(service, document.Namespace, revision);
            if (releaseRefName is not null && !string.Equals(releaseRefName, expectedRelease, StringComparison.Ordinal))
            {
                issues.Add(Schema(
                    $"platformReleaseRef.name `{releaseRefName}` does not match the expected release `{expectedRelease}`.",
                    $"Point platformReleaseRef.name at `{expectedRelease}`.",
                    "spec.relationships.platformReleaseRef.name"));
            }
        }

        if (document.HasMapping("spec", "relationships", "promotionRef"))
        {
            ValidateReferenceExists(document, byKey, issues, "spec", "relationships", "promotionRef");
        }

        // Policy-model rule from docs/operator-control-contract.md: prod must use
        // the `promote` action; lower environments must not.
        if (string.Equals(document.Namespace, "prod", StringComparison.OrdinalIgnoreCase))
        {
            if (action is not null && !string.Equals(action, "promote", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Policy(
                    $"prod ServiceBundle action `{action}` must be `promote`; prod changes advance previously validated revisions.",
                    "Set spec.deployment.action to `promote` for prod, or move this change to a lower environment.",
                    "spec.deployment.action"));
            }

            if (!document.HasMapping("spec", "relationships", "promotionRef"))
            {
                issues.Add(Policy(
                    "prod ServiceBundle has no promotionRef; prod state must be reached through a Promotion.",
                    "Add spec.relationships.promotionRef pointing at the staging-to-prod Promotion.",
                    "spec.relationships.promotionRef"));
            }
        }
    }

    private void ValidateTerraform(DesiredStateDocument document, List<DriftIssue> issues, params string[] terraformPath)
    {
        string[] repositoryPath = [.. terraformPath, "repository"];
        string[] refPath = [.. terraformPath, "ref"];
        string[] targetsPath = [.. terraformPath, "targets"];

        RequireScalar(document, issues, string.Join('.', repositoryPath), document.TryScalar(repositoryPath));
        RequireScalar(document, issues, string.Join('.', refPath), document.TryScalar(refPath));

        IReadOnlyList<string>? targets = document.TrySequence(targetsPath);
        if (targets is not { Count: > 0 })
        {
            issues.Add(Schema(
                $"{string.Join('.', targetsPath)} is missing or empty.",
                "List at least one validated runtime target.",
                string.Join('.', targetsPath)));
            return;
        }

        foreach (string target in targets)
        {
            if (!_conventions.AllowedRuntimeTargets.Contains(target, StringComparer.Ordinal))
            {
                issues.Add(new DriftIssue(
                    DriftIssueType.UnsupportedTarget,
                    DriftSeverity.Error,
                    $"Runtime target `{target}` is not in the validated allow-list.",
                    $"Use one of: {string.Join(", ", _conventions.AllowedRuntimeTargets)}.",
                    string.Join('.', targetsPath)));
            }
        }
    }

    private static void ValidateReferenceExists(
        DesiredStateDocument document,
        IReadOnlyDictionary<DesiredStateObjectKey, DesiredStateDocument> byKey,
        List<DriftIssue> issues,
        params string[] referencePath)
    {
        YamlMappingNode? reference = document.TryMapping(referencePath);
        string label = string.Join('.', referencePath);
        if (reference is null)
        {
            issues.Add(Schema($"{label} is missing.", $"Add the {label} object reference.", label));
            return;
        }

        string? kind = document.TryScalar([.. referencePath, "kind"]);
        string? name = document.TryScalar([.. referencePath, "name"]);
        string? @namespace = document.TryScalar([.. referencePath, "namespace"]);
        if (kind is null || name is null || @namespace is null)
        {
            issues.Add(Schema(
                $"{label} is missing kind/name/namespace.",
                $"Provide kind, name, and namespace under {label}.",
                label));
            return;
        }

        if (!byKey.ContainsKey(new DesiredStateObjectKey(kind, name, @namespace)))
        {
            issues.Add(Schema(
                $"{label} points at `{kind}/{name}` in `{@namespace}`, which does not exist in the desired-state tree.",
                "Create the referenced object or fix the reference name/namespace.",
                label));
        }
    }

    private void ValidateExecutionPolicyRef(
        DesiredStateDocument document,
        IReadOnlyDictionary<DesiredStateObjectKey, DesiredStateDocument> byKey,
        List<DriftIssue> issues,
        params string[] referencePath)
    {
        ValidateReferenceExists(document, byKey, issues, referencePath);

        string label = string.Join('.', referencePath);
        string? refNamespace = document.TryScalar([.. referencePath, "namespace"]);
        string? refName = document.TryScalar([.. referencePath, "name"]);

        if (refNamespace is not null && !string.Equals(refNamespace, _conventions.ControlPlaneNamespace, StringComparison.Ordinal))
        {
            issues.Add(Schema(
                $"{label}.namespace `{refNamespace}` must be `{_conventions.ControlPlaneNamespace}`.",
                $"Set {label}.namespace to `{_conventions.ControlPlaneNamespace}`.",
                $"{label}.namespace"));
        }

        // Policy-model rule: routine objects reference the approval-required
        // default policy, not the break-glass policy.
        if (refName is not null && string.Equals(refName, _conventions.ExecutionPolicyBreakGlassName, StringComparison.Ordinal))
        {
            issues.Add(Policy(
                $"{label} references the break-glass policy `{refName}`; routine objects must reference the default policy.",
                $"Set {label}.name to `{_conventions.ExecutionPolicyDefaultName}`; reserve break-glass for incident recovery.",
                $"{label}.name"));
        }
    }

    private static void RequireScalar(DesiredStateDocument document, List<DriftIssue> issues, string label, string? value)
    {
        _ = document;
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(Schema($"{label} is missing.", $"Add a value for {label}.", label));
        }
    }

    private static bool? TryBool(DesiredStateDocument document, params string[] path)
    {
        string? value = document.TryScalar(path);
        return value switch
        {
            null => null,
            _ when bool.TryParse(value, out bool parsed) => parsed,
            _ => null
        };
    }

    private static DriftIssue Schema(string detail, string fix, string fieldPath) =>
        new(DriftIssueType.SchemaMismatch, DriftSeverity.Error, detail, fix, fieldPath);

    private static DriftIssue Policy(string detail, string fix, string fieldPath) =>
        new(DriftIssueType.PolicyViolation, DriftSeverity.Error, detail, fix, fieldPath);
}
