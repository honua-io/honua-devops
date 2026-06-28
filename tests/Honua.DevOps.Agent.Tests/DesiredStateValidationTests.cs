using YamlDotNet.RepresentationModel;

namespace Honua.DevOps.Agent.Tests;

public sealed class DesiredStateValidationTests
{
    [Fact]
    public void StarterPack_HasResolvableObjectReferences()
    {
        string desiredStateRoot = ResolveDesiredStateRoot();
        string[] yamlFiles = Directory
            .GetFiles(desiredStateRoot, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        DesiredStateConventions conventions = LoadConventions(desiredStateRoot);

        Assert.NotEmpty(yamlFiles);

        Dictionary<ObjectKey, DesiredStateDocument> documents = [];
        foreach (string yamlFile in yamlFiles)
        {
            DesiredStateDocument document = LoadDocument(yamlFile);
            Assert.Equal("honua.io/v1alpha1", document.ApiVersion);

            Assert.True(
                documents.TryAdd(document.Key, document),
                $"Duplicate desired-state object `{document.Kind}/{document.Name}` in namespace `{document.Namespace}`.");
        }

        foreach ((_, DesiredStateDocument document) in documents)
        {
            ValidateDocument(document, documents, conventions);
        }
    }

    private static void ValidateDocument(
        DesiredStateDocument document,
        IReadOnlyDictionary<ObjectKey, DesiredStateDocument> documents,
        DesiredStateConventions conventions)
    {
        switch (document.Kind)
        {
            case "PlatformStack":
                ValidatePlatformStack(document, conventions);
                break;
            case "ExecutionPolicy":
                ValidateExecutionPolicy(document, conventions);
                break;
            case "PlatformRelease":
                ValidatePlatformRelease(document, conventions);
                break;
            case "Promotion":
                ValidatePromotion(document, documents, conventions);
                break;
            case "ServiceBundle":
                ValidateServiceBundle(document, documents, conventions);
                break;
            case "OperationalWorkflow":
                ValidateOperationalWorkflow(document);
                break;
            default:
                throw new Xunit.Sdk.XunitException(
                    $"Unsupported desired-state kind `{document.Kind}` in `{document.Path}`.");
        }
    }

    // OperationalWorkflow: the declarative catalog of operations the AI DevOps operator may run.
    // Fails closed unless the workflow is rollback-safe — a non-empty rollback.procedure AND
    // rollback.verify — because you do not let an autonomous operator run an action it cannot prove
    // it can undo. This complements the ExecutionPolicy `rollback-intent` required-check (intent
    // declared) by requiring the rollback be specified + verifiable per workflow.
    private static readonly string[] OperationalWorkflowCategories =
        ["data-ops", "platform-ops", "gp-ops", "release-ops"];

    private static readonly string[] OperationalWorkflowAutonomyTiers = ["1", "2", "3"];

    private static void ValidateOperationalWorkflow(DesiredStateDocument document)
    {
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "intent")));
        Assert.Contains(document.RequireScalar("spec", "category"), OperationalWorkflowCategories);
        Assert.Contains(document.RequireScalar("spec", "autonomyTier"), OperationalWorkflowAutonomyTiers);
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "executionPolicyRef")));

        Assert.NotEmpty(document.RequireSequence("spec", "preconditions"));
        Assert.NotEmpty(document.RequireSequence("spec", "steps"));
        Assert.NotEmpty(document.RequireSequence("spec", "verify"));

        // The safety rule: a verifiable rollback is mandatory.
        Assert.NotEmpty(document.RequireSequence("spec", "rollback", "procedure"));
        Assert.NotEmpty(document.RequireSequence("spec", "rollback", "verify"));

        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "integrationTest")));
    }

    private static void ValidatePlatformStack(DesiredStateDocument document, DesiredStateConventions conventions)
    {
        string environment = document.RequireScalar("spec", "environment");
        Assert.Equal(document.Namespace, environment);
        Assert.Equal($"{conventions.PlatformStackPrefix}-{environment}", document.Name);
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "terraform", "repository")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "terraform", "ref")));
        IReadOnlyList<string> targets = document.RequireSequence("spec", "terraform", "targets");
        Assert.NotEmpty(targets);
        foreach (string target in targets)
        {
            Assert.Contains(
                target,
                conventions.AllowedRuntimeTargets);
        }

        Assert.NotEmpty(document.RequireSequence("spec", "secretRefs"));
    }

    private static void ValidateExecutionPolicy(DesiredStateDocument document, DesiredStateConventions conventions)
    {
        Assert.Equal(conventions.ControlPlaneNamespace, document.Namespace);
        Assert.True(
            document.Name is var name &&
            (name == conventions.ExecutionPolicyDefaultName || name == conventions.ExecutionPolicyBreakGlassName),
            $"ExecutionPolicy `{document.Path}` must use a shared policy name from desired-state conventions.");
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "executionMode")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "executionTier")));
        Assert.NotEmpty(document.RequireSequence("spec", "allowedEnvironments"));
        Assert.NotEmpty(document.RequireSequence("spec", "requiredChecks"));
    }

    private static void ValidatePlatformRelease(DesiredStateDocument document, DesiredStateConventions conventions)
    {
        string service = document.RequireScalar("spec", "service");
        Assert.False(string.IsNullOrWhiteSpace(service));
        Assert.Equal(document.Namespace, document.RequireScalar("spec", "environment"));
        string revision = document.RequireScalar("spec", "revision");
        Assert.False(string.IsNullOrWhiteSpace(revision));
        Assert.Equal(
            ApplyTemplate(
                conventions.PlatformReleaseNameTemplate,
                ("service", NormalizeToken(service)),
                ("environment", document.Namespace),
                ("revision", NormalizeToken(revision))),
            document.Name);
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "action")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "gitOpsTool")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "terraform", "repository")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "terraform", "ref")));
        Assert.NotEmpty(document.RequireSequence("spec", "terraform", "targets"));
    }

    private static void ValidatePromotion(
        DesiredStateDocument document,
        IReadOnlyDictionary<ObjectKey, DesiredStateDocument> documents,
        DesiredStateConventions conventions)
    {
        string service = document.RequireScalar("spec", "service");
        Assert.False(string.IsNullOrWhiteSpace(service));

        string sourceEnvironment = document.RequireScalar("spec", "sourceEnvironment");
        string targetEnvironment = document.RequireScalar("spec", "targetEnvironment");
        Assert.False(string.IsNullOrWhiteSpace(sourceEnvironment));
        Assert.False(string.IsNullOrWhiteSpace(targetEnvironment));
        Assert.Equal(targetEnvironment, document.Namespace);
        Assert.Equal(
            ApplyTemplate(
                conventions.PromotionNameTemplate,
                ("service", NormalizeToken(service)),
                ("source", sourceEnvironment),
                ("target", targetEnvironment)),
            document.Name);

        AssertReferenceExists(document, documents, "spec", "executionPolicyRef");
        Assert.Equal(
            conventions.ControlPlaneNamespace,
            document.RequireScalar("spec", "executionPolicyRef", "namespace"));
        Assert.Equal(
            conventions.ExecutionPolicyDefaultName,
            document.RequireScalar("spec", "executionPolicyRef", "name"));
    }

    private static void ValidateServiceBundle(
        DesiredStateDocument document,
        IReadOnlyDictionary<ObjectKey, DesiredStateDocument> documents,
        DesiredStateConventions conventions)
    {
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "description")));
        Assert.Equal(document.Namespace, document.RequireScalar("spec", "deployment", "environment"));
        string service = document.RequireScalar("spec", "deployment", "service");
        Assert.False(string.IsNullOrWhiteSpace(service));
        Assert.Equal(
            ApplyTemplate(
                conventions.ServiceBundleNameTemplate,
                ("service", NormalizeToken(service)),
                ("environment", document.Namespace)),
            document.Name);
        string revision = document.RequireScalar("spec", "deployment", "revision");
        Assert.False(string.IsNullOrWhiteSpace(revision));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "deployment", "action")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "deployment", "gitOpsTool")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "deployment", "terraform", "repository")));
        Assert.False(string.IsNullOrWhiteSpace(document.RequireScalar("spec", "deployment", "terraform", "ref")));
        IReadOnlyList<string> targets = document.RequireSequence("spec", "deployment", "terraform", "targets");
        Assert.NotEmpty(targets);
        foreach (string target in targets)
        {
            Assert.Contains(
                target,
                conventions.AllowedRuntimeTargets);
        }

        AssertReferenceExists(document, documents, "spec", "relationships", "platformStackRef");
        AssertReferenceExists(document, documents, "spec", "relationships", "platformReleaseRef");
        AssertReferenceExists(document, documents, "spec", "relationships", "executionPolicyRef");
        Assert.Equal(
            ApplyTemplate(
                conventions.PlatformReleaseNameTemplate,
                ("service", NormalizeToken(service)),
                ("environment", document.Namespace),
                ("revision", NormalizeToken(revision))),
            document.RequireScalar("spec", "relationships", "platformReleaseRef", "name"));
        Assert.Equal(
            conventions.ControlPlaneNamespace,
            document.RequireScalar("spec", "relationships", "executionPolicyRef", "namespace"));
        Assert.Equal(
            conventions.ExecutionPolicyDefaultName,
            document.RequireScalar("spec", "relationships", "executionPolicyRef", "name"));

        if (document.TryGetMapping(out _, "spec", "relationships", "promotionRef"))
        {
            AssertReferenceExists(document, documents, "spec", "relationships", "promotionRef");
        }
    }

    private static void AssertReferenceExists(
        DesiredStateDocument document,
        IReadOnlyDictionary<ObjectKey, DesiredStateDocument> documents,
        params string[] path)
    {
        YamlMappingNode reference = document.RequireMapping(path);
        string kind = GetRequiredScalar(reference, "kind", document.Path);
        string name = GetRequiredScalar(reference, "name", document.Path);
        string @namespace = GetRequiredScalar(reference, "namespace", document.Path);

        Assert.True(
            documents.ContainsKey(new ObjectKey(kind, name, @namespace)),
            $"Missing desired-state reference `{kind}/{name}` in namespace `{@namespace}` from `{document.Path}`.");
    }

    private static DesiredStateDocument LoadDocument(string path)
    {
        using StreamReader reader = File.OpenText(path);
        YamlStream yaml = new();
        yaml.Load(reader);

        Assert.Single(yaml.Documents);

        YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;
        string apiVersion = GetRequiredScalar(root, "apiVersion", path);
        string kind = GetRequiredScalar(root, "kind", path);
        YamlMappingNode metadata = GetRequiredMapping(root, "metadata", path);
        string name = GetRequiredScalar(metadata, "name", path);
        string @namespace = GetRequiredScalar(metadata, "namespace", path);

        return new DesiredStateDocument(
            Path: path,
            ApiVersion: apiVersion,
            Kind: kind,
            Name: name,
            Namespace: @namespace,
            Root: root);
    }

    private static string ResolveDesiredStateRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("HONUA_DEVOPS_DESIRED_STATE_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            string resolvedOverride = Path.GetFullPath(overrideRoot);
            Assert.True(Directory.Exists(resolvedOverride), $"Desired-state root not found: `{resolvedOverride}`.");
            return resolvedOverride;
        }

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string desiredStateRoot = Path.Combine(repoRoot, "desired-state");

        Assert.True(Directory.Exists(desiredStateRoot), $"Desired-state root not found: `{desiredStateRoot}`.");
        return desiredStateRoot;
    }

    private static DesiredStateConventions LoadConventions(string desiredStateRoot)
    {
        string conventionsPath = Path.Combine(desiredStateRoot, "conventions.env");
        Assert.True(File.Exists(conventionsPath), $"Desired-state conventions file not found: `{conventionsPath}`.");

        Dictionary<string, string> values = File.ReadAllLines(conventionsPath)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line =>
            {
                int separator = line.IndexOf('=');
                Assert.True(separator > 0, $"Invalid conventions line `{line}` in `{conventionsPath}`.");
                return new KeyValuePair<string, string>(
                    line[..separator].Trim(),
                    line[(separator + 1)..].Trim());
            })
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

        return new DesiredStateConventions(
            AllowedRuntimeTargets: RequireConventionList(values, "ALLOWED_RUNTIME_TARGETS"),
            ControlPlaneNamespace: RequireConvention(values, "CONTROL_PLANE_NAMESPACE"),
            PlatformStackPrefix: RequireConvention(values, "PLATFORM_STACK_PREFIX"),
            ExecutionPolicyDefaultName: RequireConvention(values, "EXECUTION_POLICY_DEFAULT_NAME"),
            ExecutionPolicyBreakGlassName: RequireConvention(values, "EXECUTION_POLICY_BREAK_GLASS_NAME"),
            PlatformReleaseNameTemplate: RequireConvention(values, "PLATFORM_RELEASE_NAME_TEMPLATE"),
            PromotionNameTemplate: RequireConvention(values, "PROMOTION_NAME_TEMPLATE"),
            ServiceBundleNameTemplate: RequireConvention(values, "SERVICE_BUNDLE_NAME_TEMPLATE"));
    }

    private static IReadOnlyList<string> RequireConventionList(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return RequireConvention(values, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string RequireConvention(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        Assert.True(values.TryGetValue(key, out string? value), $"Missing desired-state convention `{key}`.");
        Assert.False(string.IsNullOrWhiteSpace(value), $"Desired-state convention `{key}` must not be empty.");
        return value;
    }

    private static string NormalizeToken(string value)
    {
        string normalized = System.Text.RegularExpressions.Regex
            .Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-")
            .Trim('-');

        return string.IsNullOrWhiteSpace(normalized) ? "item" : normalized;
    }

    private static string ApplyTemplate(string template, params (string Key, string Value)[] replacements)
    {
        string rendered = template;
        foreach ((string key, string value) in replacements)
        {
            rendered = rendered.Replace($"{{{key}}}", value, StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string GetRequiredScalar(YamlMappingNode mapping, string key, string path)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ||
            node is not YamlScalarNode scalar ||
            string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new Xunit.Sdk.XunitException($"Expected scalar `{key}` in `{path}`.");
        }

        return scalar.Value;
    }

    private static YamlMappingNode GetRequiredMapping(YamlMappingNode mapping, string key, string path)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ||
            node is not YamlMappingNode child)
        {
            throw new Xunit.Sdk.XunitException($"Expected mapping `{key}` in `{path}`.");
        }

        return child;
    }

    private sealed record DesiredStateDocument(
        string Path,
        string ApiVersion,
        string Kind,
        string Name,
        string Namespace,
        YamlMappingNode Root)
    {
        internal ObjectKey Key => new(Kind, Name, Namespace);

        internal string RequireScalar(params string[] path)
        {
            YamlNode node = Traverse(path);
            if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new Xunit.Sdk.XunitException($"Expected scalar `{string.Join(".", path)}` in `{Path}`.");
            }

            return scalar.Value;
        }

        internal IReadOnlyList<string> RequireSequence(params string[] path)
        {
            YamlNode node = Traverse(path);
            if (node is not YamlSequenceNode sequence || sequence.Children.Count == 0)
            {
                throw new Xunit.Sdk.XunitException($"Expected non-empty sequence `{string.Join(".", path)}` in `{Path}`.");
            }

            return sequence.Children
                .OfType<YamlScalarNode>()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }

        internal YamlMappingNode RequireMapping(params string[] path)
        {
            YamlNode node = Traverse(path);
            if (node is not YamlMappingNode mapping)
            {
                throw new Xunit.Sdk.XunitException($"Expected mapping `{string.Join(".", path)}` in `{Path}`.");
            }

            return mapping;
        }

        internal bool TryGetMapping(out YamlMappingNode mapping, params string[] path)
        {
            mapping = null!;
            YamlNode current = Root;
            foreach (string segment in path)
            {
                if (current is not YamlMappingNode currentMapping ||
                    !currentMapping.Children.TryGetValue(new YamlScalarNode(segment), out YamlNode? nextNode) ||
                    nextNode is null)
                {
                    return false;
                }

                current = nextNode;
            }

            if (current is not YamlMappingNode resolved)
            {
                return false;
            }

            mapping = resolved;
            return true;
        }

        private YamlNode Traverse(params string[] path)
        {
            YamlNode current = Root;
            foreach (string segment in path)
            {
                if (current is not YamlMappingNode mapping ||
                    !mapping.Children.TryGetValue(new YamlScalarNode(segment), out YamlNode? nextNode) ||
                    nextNode is null)
                {
                    throw new Xunit.Sdk.XunitException($"Expected path `{string.Join(".", path)}` in `{Path}`.");
                }

                current = nextNode;
            }

            return current;
        }
    }

    private sealed record DesiredStateConventions(
        IReadOnlyList<string> AllowedRuntimeTargets,
        string ControlPlaneNamespace,
        string PlatformStackPrefix,
        string ExecutionPolicyDefaultName,
        string ExecutionPolicyBreakGlassName,
        string PlatformReleaseNameTemplate,
        string PromotionNameTemplate,
        string ServiceBundleNameTemplate);

    private sealed record ObjectKey(string Kind, string Name, string Namespace);
}
