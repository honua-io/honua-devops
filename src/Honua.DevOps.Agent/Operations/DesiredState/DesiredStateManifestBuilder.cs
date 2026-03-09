namespace Honua.DevOps.Agent.Operations.DesiredState;

internal static class DesiredStateManifestBuilder
{
    internal static ManifestApplyRequest BuildGitOpsDeployRequest(
        string service,
        IReadOnlyList<string> environments,
        string revision,
        string action,
        string changeSummary,
        string gitOpsTool,
        string terraformRepository,
        string terraformRef,
        IReadOnlyList<string> deploymentTargets,
        bool dryRun,
        ExecutionMode executionMode,
        ExecutionTier executionTier,
        IReadOnlyList<string> allowedEnvironments)
    {
        ServiceBundleManifestResource[] resources = environments
            .Select((environment, index) =>
            {
                string normalizedService = NormalizeResourceToken(service, "honua-service");
                string normalizedEnvironment = NormalizeResourceToken(environment, "default");
                string normalizedRevisionToken = NormalizeResourceToken(revision, "head");
                string normalizedChangeSummary = NormalizeText(changeSummary, "not provided");

                PlatformReleaseSpec release = new(
                    Service: normalizedService,
                    Environment: normalizedEnvironment,
                    Revision: revision,
                    Action: action,
                    ChangeSummary: normalizedChangeSummary,
                    GitOpsTool: gitOpsTool,
                    Terraform: new TerraformSource(terraformRepository, terraformRef, deploymentTargets));

                DesiredStateObjectReference platformStackRef = new(
                    DesiredStateApi.ApiVersion,
                    DesiredStateApi.PlatformStackKind,
                    $"platform-stack-{normalizedEnvironment}",
                    normalizedEnvironment);
                DesiredStateObjectReference platformReleaseRef = new(
                    DesiredStateApi.ApiVersion,
                    DesiredStateApi.PlatformReleaseKind,
                    $"{normalizedService}-{normalizedEnvironment}-{normalizedRevisionToken}",
                    normalizedEnvironment);
                DesiredStateObjectReference executionPolicyRef = new(
                    DesiredStateApi.ApiVersion,
                    DesiredStateApi.ExecutionPolicyKind,
                    $"execution-policy-{executionTier.ToConfigValue()}",
                    normalizedEnvironment);
                DesiredStateObjectReference? promotionRef = index == 0
                    ? null
                    : new DesiredStateObjectReference(
                        DesiredStateApi.ApiVersion,
                        DesiredStateApi.PromotionKind,
                        $"{normalizedService}-{environments[index - 1]}-to-{normalizedEnvironment}",
                        normalizedEnvironment);

                return new ServiceBundleManifestResource(
                    ApiVersion: DesiredStateApi.ApiVersion,
                    Kind: DesiredStateApi.BootstrapManifestKind,
                    Metadata: new DesiredStateMetadata(
                        Name: $"{normalizedService}-{normalizedEnvironment}",
                        Namespace: normalizedEnvironment,
                        Labels: new Dictionary<string, string>
                        {
                            ["managed-by"] = "honua-devops",
                            ["service"] = normalizedService,
                            ["environment"] = normalizedEnvironment
                        },
                        Annotations: new Dictionary<string, string>
                        {
                            ["honua.devops/action"] = action,
                            ["honua.devops/revision"] = revision,
                            ["honua.devops/gitops-tool"] = gitOpsTool,
                            ["honua.devops/change-summary"] = normalizedChangeSummary,
                            ["honua.devops/terraform-ref"] = terraformRef,
                            ["honua.devops/execution-mode"] = executionMode.ToString().ToLowerInvariant(),
                            ["honua.devops/execution-tier"] = executionTier.ToConfigValue(),
                            ["honua.devops/dry-run"] = dryRun ? "true" : "false",
                            ["honua.devops/canonical-kind"] = DesiredStateApi.ServiceBundleKind,
                            ["honua.devops/requested-at"] = DateTimeOffset.UtcNow.ToString("O")
                        }),
                    Spec: new ServiceBundleSpec(
                        Description: $"GitOps deployment request for {normalizedService} in {normalizedEnvironment}.",
                        Srid: 4326,
                        Deployment: release,
                        Relationships: new ServiceBundleRelationships(
                            PlatformStackRef: platformStackRef,
                            PlatformReleaseRef: platformReleaseRef,
                            ExecutionPolicyRef: executionPolicyRef,
                            PromotionRef: promotionRef)));
            })
            .ToArray();

        bool prune = action.Contains("prune", StringComparison.OrdinalIgnoreCase);
        return new ManifestApplyRequest(resources, dryRun, prune);
    }

    private static string NormalizeResourceToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        System.Text.StringBuilder builder = new();
        bool appendHyphen = false;

        foreach (char character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                appendHyphen = true;
                continue;
            }

            if (appendHyphen)
            {
                builder.Append('-');
                appendHyphen = false;
            }
        }

        string normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
