namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal sealed class AzureFunctionsRuntimeAdapter : RuntimeAdapterBase
{
    public override RuntimeAdapterCapability Capability => RuntimeAdapterCatalog.BuildServerlessCapability("azure-functions");

    protected override string TerraformModule => "azure-functions";

    protected override string ReleaseBackend => "honua-gitops";

    protected override string RollbackHandle => "function-slot-or-revision";
}

internal sealed class AwsLambdaRuntimeAdapter : RuntimeAdapterBase
{
    public override RuntimeAdapterCapability Capability => RuntimeAdapterCatalog.BuildServerlessCapability("lambda");

    protected override string TerraformModule => "aws-serverless";

    protected override string ReleaseBackend => "honua-gitops";

    protected override string RollbackHandle => "lambda-alias-weight-shift";
}

internal sealed class AksRuntimeAdapter : RuntimeAdapterBase
{
    public override RuntimeAdapterCapability Capability => RuntimeAdapterCatalog.BuildKubernetesCapability("aks");

    protected override string TerraformModule => "azure-aks";

    protected override string ReleaseBackend => "honua-helm";

    protected override string RollbackHandle => "helm-rollback";

    protected override string BuildReleasePlanCommand(RuntimeAdapterRequest request)
    {
        return $"helm upgrade --install honua-{ShellQuote(request.Service)} honua/honua-server --namespace <env> --dry-run";
    }

    protected override string BuildReleaseApplyCommand(RuntimeAdapterRequest request)
    {
        return $"helm upgrade --install honua-{ShellQuote(request.Service)} honua/honua-server --namespace <env>";
    }

    protected override string BuildRollbackCommand(RuntimeAdapterRequest request)
    {
        return $"helm rollback honua-{ShellQuote(request.Service)} <revision>";
    }
}

internal sealed class EksRuntimeAdapter : RuntimeAdapterBase
{
    public override RuntimeAdapterCapability Capability => RuntimeAdapterCatalog.BuildKubernetesCapability("eks");

    protected override string TerraformModule => "aws-eks";

    protected override string ReleaseBackend => "honua-helm";

    protected override string RollbackHandle => "helm-rollback";

    protected override string BuildReleasePlanCommand(RuntimeAdapterRequest request)
    {
        return $"helm upgrade --install honua-{ShellQuote(request.Service)} honua/honua-server --namespace <env> --dry-run";
    }

    protected override string BuildReleaseApplyCommand(RuntimeAdapterRequest request)
    {
        return $"helm upgrade --install honua-{ShellQuote(request.Service)} honua/honua-server --namespace <env>";
    }

    protected override string BuildRollbackCommand(RuntimeAdapterRequest request)
    {
        return $"helm rollback honua-{ShellQuote(request.Service)} <revision>";
    }
}

internal sealed class AwsEcsRuntimeAdapter : RuntimeAdapterBase
{
    public override RuntimeAdapterCapability Capability => RuntimeAdapterCatalog.BuildManagedContainerCapability("ecs");

    protected override string TerraformModule => "aws-ecs";

    protected override string ReleaseBackend => "honua-gitops";

    protected override string RollbackHandle => "ecs-service-deployment-rollback";
}

internal sealed class AzureContainerAppsRuntimeAdapter : RuntimeAdapterBase
{
    public override RuntimeAdapterCapability Capability => RuntimeAdapterCatalog.BuildManagedContainerCapability("aca");

    protected override string TerraformModule => "azure-aca";

    protected override string ReleaseBackend => "honua-gitops";

    protected override string RollbackHandle => "aca-revision-rollback";
}
