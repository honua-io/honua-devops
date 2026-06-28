using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.DesiredState;

internal static class DesiredStateApi
{
    internal const string ApiVersion = "honua.io/v1alpha1";
    internal const string BootstrapManifestKind = "Service";
    internal const string PlatformStackKind = "PlatformStack";
    internal const string PlatformReleaseKind = "PlatformRelease";
    internal const string ServiceBundleKind = "ServiceBundle";
    internal const string PromotionKind = "Promotion";
    internal const string ExecutionPolicyKind = "ExecutionPolicy";

    // Canonical desired-state conventions (mirrors desired-state/conventions.env).
    // ExecutionPolicy objects live in the shared control-plane namespace, not the
    // per-environment namespace, and resolve to a single default object (the
    // break-glass tier being the only exception that maps to a distinct object).
    internal const string ControlPlaneNamespace = "control-plane";
    internal const string ExecutionPolicyDefaultName = "execution-policy-default";
    internal const string ExecutionPolicyBreakGlassName = "execution-policy-break-glass";
}

internal sealed record ManifestApplyRequest(
    [property: JsonPropertyName("resources")] IReadOnlyList<ServiceBundleManifestResource> Resources,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("prune")] bool Prune);

internal sealed record ServiceBundleManifestResource(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("metadata")] DesiredStateMetadata Metadata,
    [property: JsonPropertyName("spec")] ServiceBundleSpec Spec);

internal sealed record DesiredStateMetadata(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels,
    [property: JsonPropertyName("annotations")] IReadOnlyDictionary<string, string> Annotations);

internal sealed record DesiredStateObjectReference(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("namespace")] string Namespace);

internal sealed record TerraformSource(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("targets")] IReadOnlyList<string> Targets);

internal sealed record ServiceBundleSpec(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("srid")] int Srid,
    [property: JsonPropertyName("deployment")] PlatformReleaseSpec Deployment,
    [property: JsonPropertyName("relationships")] ServiceBundleRelationships Relationships);

internal sealed record ServiceBundleRelationships(
    [property: JsonPropertyName("platformStackRef")] DesiredStateObjectReference PlatformStackRef,
    [property: JsonPropertyName("platformReleaseRef")] DesiredStateObjectReference PlatformReleaseRef,
    [property: JsonPropertyName("executionPolicyRef")] DesiredStateObjectReference ExecutionPolicyRef,
    [property: JsonPropertyName("promotionRef")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DesiredStateObjectReference? PromotionRef);

internal sealed record PlatformStackSpec(
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("terraform")] TerraformSource Terraform,
    [property: JsonPropertyName("secretRefs")] IReadOnlyList<string> SecretRefs);

internal sealed record PlatformReleaseSpec(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("changeSummary")] string ChangeSummary,
    [property: JsonPropertyName("gitOpsTool")] string GitOpsTool,
    [property: JsonPropertyName("terraform")] TerraformSource Terraform);

internal sealed record PromotionSpec(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("sourceEnvironment")] string SourceEnvironment,
    [property: JsonPropertyName("targetEnvironment")] string TargetEnvironment,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("executionPolicyRef")] DesiredStateObjectReference ExecutionPolicyRef);

internal sealed record ExecutionPolicySpec(
    [property: JsonPropertyName("executionMode")] string ExecutionMode,
    [property: JsonPropertyName("executionTier")] string ExecutionTier,
    [property: JsonPropertyName("allowedEnvironments")] IReadOnlyList<string> AllowedEnvironments,
    [property: JsonPropertyName("requiredChecks")] IReadOnlyList<string> RequiredChecks,
    [property: JsonPropertyName("requiresApproval")] bool RequiresApproval,
    [property: JsonPropertyName("allowsBreakGlass")] bool AllowsBreakGlass);
