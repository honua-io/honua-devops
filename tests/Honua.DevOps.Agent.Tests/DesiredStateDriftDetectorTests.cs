using Honua.DevOps.Agent.Operations.DesiredState;

namespace Honua.DevOps.Agent.Tests;

public sealed class DesiredStateDriftDetectorTests
{
    private static DesiredStateConventions Conventions() => DesiredStateConventions.Parse(
        """
        ALLOWED_RUNTIME_TARGETS=azure-functions,lambda,aks,eks,ecs,aca
        CONTROL_PLANE_NAMESPACE=control-plane
        PLATFORM_STACK_PREFIX=platform-stack
        EXECUTION_POLICY_DEFAULT_NAME=execution-policy-default
        EXECUTION_POLICY_BREAK_GLASS_NAME=execution-policy-break-glass
        PLATFORM_RELEASE_NAME_TEMPLATE={service}-{environment}-{revision}
        PROMOTION_NAME_TEMPLATE={service}-{source}-to-{target}
        SERVICE_BUNDLE_NAME_TEMPLATE={service}-{environment}
        """);

    private static DesiredStateDriftReport Detect(params (string Path, string Yaml)[] objects)
    {
        DesiredStateDocument[] documents = objects
            .Select(item => DesiredStateDocument.Load(item.Path, item.Yaml))
            .ToArray();
        return new DesiredStateDriftDetector(Conventions()).Detect("/fixtures", documents);
    }

    private const string ValidPlatformStackDev =
        """
        apiVersion: honua.io/v1alpha1
        kind: PlatformStack
        metadata:
          name: platform-stack-dev
          namespace: dev
        spec:
          environment: dev
          terraform:
            repository: https://github.com/honua-io/honua-terraform
            ref: main
            targets:
              - eks
          secretRefs:
            - HONUA_ADMIN_API_KEY
        """;

    private const string ValidDefaultPolicy =
        """
        apiVersion: honua.io/v1alpha1
        kind: ExecutionPolicy
        metadata:
          name: execution-policy-default
          namespace: control-plane
        spec:
          executionMode: plan
          executionTier: plan
          allowedEnvironments:
            - dev
          requiredChecks:
            - manifest-diff
          requiresApproval: true
          allowsBreakGlass: false
        """;

    private const string ValidPlatformReleaseDev =
        """
        apiVersion: honua.io/v1alpha1
        kind: PlatformRelease
        metadata:
          name: roads-api-dev-release-2026-03
          namespace: dev
        spec:
          service: roads-api
          environment: dev
          revision: release/2026.03
          action: sync
          gitOpsTool: honua-gitops
          terraform:
            repository: https://github.com/honua-io/honua-terraform
            ref: main
            targets:
              - eks
        """;

    private static string ValidServiceBundleDev(string action = "sync") =>
        $"""
        apiVersion: honua.io/v1alpha1
        kind: ServiceBundle
        metadata:
          name: roads-api-dev
          namespace: dev
        spec:
          description: Bootstrap ServiceBundle for roads-api in dev.
          srid: 4326
          deployment:
            service: roads-api
            environment: dev
            revision: release/2026.03
            action: {action}
            gitOpsTool: honua-gitops
            terraform:
              repository: https://github.com/honua-io/honua-terraform
              ref: main
              targets:
                - eks
          relationships:
            platformStackRef:
              apiVersion: honua.io/v1alpha1
              kind: PlatformStack
              name: platform-stack-dev
              namespace: dev
            platformReleaseRef:
              apiVersion: honua.io/v1alpha1
              kind: PlatformRelease
              name: roads-api-dev-release-2026-03
              namespace: dev
            executionPolicyRef:
              apiVersion: honua.io/v1alpha1
              kind: ExecutionPolicy
              name: execution-policy-default
              namespace: control-plane
        """;

    [Fact]
    public void ValidObjectSet_ProducesNoIssues()
    {
        DesiredStateDriftReport report = Detect(
            ("dev.platformstack.yaml", ValidPlatformStackDev),
            ("default.executionpolicy.yaml", ValidDefaultPolicy),
            ("dev.platformrelease.yaml", ValidPlatformReleaseDev),
            ("dev.servicebundle.yaml", ValidServiceBundleDev()));

        Assert.True(report.IsClean, FormatIssues(report));
        Assert.Equal(4, report.ObjectsScanned);
        Assert.Equal(0, report.IssueCount);
    }

    [Fact]
    public void SchemaMismatchObject_FlagsMissingFieldAndNameDrift()
    {
        // Name violates the prefix convention and spec.terraform.ref is missing.
        const string brokenStack =
            """
            apiVersion: honua.io/v1alpha1
            kind: PlatformStack
            metadata:
              name: wrong-name-dev
              namespace: dev
            spec:
              environment: dev
              terraform:
                repository: https://github.com/honua-io/honua-terraform
                targets:
                  - eks
              secretRefs:
                - HONUA_ADMIN_API_KEY
            """;

        DesiredStateDriftReport report = Detect(("dev.platformstack.yaml", brokenStack));

        Assert.False(report.IsClean);
        Assert.True(report.SchemaMismatchCount >= 2, FormatIssues(report));
        Assert.Equal(0, report.PolicyViolationCount);
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.SchemaMismatch && issue.FieldPath == "spec.terraform.ref");
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.SchemaMismatch && issue.FieldPath == "metadata.name");
        Assert.All(report.AllIssues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.SuggestedFix)));
    }

    [Fact]
    public void UnsupportedTarget_IsClassifiedSeparately()
    {
        const string badTargetStack =
            """
            apiVersion: honua.io/v1alpha1
            kind: PlatformStack
            metadata:
              name: platform-stack-dev
              namespace: dev
            spec:
              environment: dev
              terraform:
                repository: https://github.com/honua-io/honua-terraform
                ref: main
                targets:
                  - gke
              secretRefs:
                - HONUA_ADMIN_API_KEY
            """;

        DesiredStateDriftReport report = Detect(("dev.platformstack.yaml", badTargetStack));

        Assert.Equal(1, report.UnsupportedTargetCount);
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.UnsupportedTarget && issue.Detail.Contains("gke"));
    }

    [Fact]
    public void PolicyViolation_DefaultPolicyWithoutApproval_IsFlagged()
    {
        const string laxPolicy =
            """
            apiVersion: honua.io/v1alpha1
            kind: ExecutionPolicy
            metadata:
              name: execution-policy-default
              namespace: control-plane
            spec:
              executionMode: execute
              executionTier: execute-lower-env
              allowedEnvironments:
                - dev
              requiredChecks:
                - manifest-diff
              requiresApproval: false
              allowsBreakGlass: true
            """;

        DesiredStateDriftReport report = Detect(("default.executionpolicy.yaml", laxPolicy));

        Assert.True(report.PolicyViolationCount >= 3, FormatIssues(report));
        Assert.Equal(0, report.SchemaMismatchCount);
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.PolicyViolation && issue.FieldPath == "spec.requiresApproval");
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.PolicyViolation && issue.FieldPath == "spec.allowsBreakGlass");
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.PolicyViolation && issue.FieldPath == "spec.executionMode");
    }

    [Fact]
    public void PolicyViolation_RoutineObjectReferencingBreakGlass_IsFlagged()
    {
        string bundle = ValidServiceBundleDev()
            .Replace("name: execution-policy-default", "name: execution-policy-break-glass");

        DesiredStateDriftReport report = Detect(
            ("dev.platformstack.yaml", ValidPlatformStackDev),
            ("dev.platformrelease.yaml", ValidPlatformReleaseDev),
            ("dev.servicebundle.yaml", bundle));

        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.PolicyViolation &&
            issue.Detail.Contains("break-glass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PolicyViolation_ProdBundleWithoutPromote_IsFlagged()
    {
        // prod ServiceBundle using `sync` action and no promotionRef.
        string prodBundle = ValidServiceBundleDev(action: "sync")
            .Replace("namespace: dev", "namespace: prod")
            .Replace("name: roads-api-dev", "name: roads-api-prod")
            .Replace("environment: dev", "environment: prod")
            .Replace("name: platform-stack-dev", "name: platform-stack-prod")
            .Replace("name: roads-api-dev-release-2026-03", "name: roads-api-prod-release-2026-03");

        DesiredStateDriftReport report = Detect(("prod.servicebundle.yaml", prodBundle));

        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.PolicyViolation && issue.FieldPath == "spec.deployment.action");
        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.PolicyViolation && issue.FieldPath == "spec.relationships.promotionRef");
    }

    [Fact]
    public void UnparseableFile_IsReportedAsSchemaMismatch()
    {
        DesiredStateDriftReport report = Detect(("broken.yaml", "key: : : not: valid: yaml: ["));

        Assert.Equal(1, report.FilesFailedToParse);
        Assert.Contains(report.AllIssues, issue => issue.IssueType == DriftIssueType.SchemaMismatch);
    }

    [Fact]
    public void MissingReference_IsFlaggedSchemaMismatch()
    {
        // ServiceBundle with no PlatformStack/PlatformRelease objects present.
        DesiredStateDriftReport report = Detect(("dev.servicebundle.yaml", ValidServiceBundleDev()));

        Assert.Contains(report.AllIssues, issue =>
            issue.IssueType == DriftIssueType.SchemaMismatch &&
            issue.FieldPath == "spec.relationships.platformStackRef");
    }

    [Fact]
    public void CheckedInStarterPack_HasNoDrift()
    {
        string root = ResolveDesiredStateRoot();
        DesiredStateDriftReport report = DesiredStateDriftDetector.DetectFromDirectory(root);

        Assert.True(report.IsClean, FormatIssues(report));
        Assert.True(report.ObjectsScanned > 0);
    }

    private static string ResolveDesiredStateRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("HONUA_DEVOPS_DESIRED_STATE_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "desired-state");
    }

    private static string FormatIssues(DesiredStateDriftReport report) =>
        string.Join(
            Environment.NewLine,
            report.Remediations
                .Where(remediation => !remediation.IsClean)
                .SelectMany(remediation => remediation.Issues
                    .Select(issue => $"{remediation.Path} [{issue.IssueType}] {issue.FieldPath}: {issue.Detail}")));
}
