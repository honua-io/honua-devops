namespace Honua.DevOps.Agent.Operations;

/// <summary>Host-selected substrate identity; model arguments cannot choose a different backend or root.</summary>
internal sealed record ProvisioningRuntimeProfile(string RuntimeKind, string TerraformTarget, string StackId,
    string TerraformRootName, string TargetKind, string Backend)
{
    internal const string EnvironmentVariable = "HONUA_DEVOPS_RUNTIME_KIND";
    internal static ProvisioningRuntimeProfile Resolve(string? runtimeKind) => runtimeKind switch
    {
        null or "" or "aws-ecs" => new("aws-ecs", "ecs", "aws-ecs", "aws", "AwsEcs", "honua-aws-ecs-alb"),
        "aws-lambda" => new("aws-lambda", "lambda", "aws-lambda", "aws-serverless", "AwsLambda", "honua-gitops-aws-lambda"),
        "aws-eks" => new("aws-eks", "eks", "aws-eks", "aws-eks-native", "Kubernetes", "honua-kubernetes-deployment"),
        _ => throw new InvalidOperationException("The host-configured provisioning runtime is unsupported.")
    };
}
