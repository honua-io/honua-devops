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

    protected override string ReleaseBackend => "honua-kubernetes-deployment";

    protected override string RollbackHandle => "server-sealed-protected-recovery";

    public override RuntimeAdapterStepPlan Validate(RuntimeAdapterRequest request) => Guidance(
        base.Validate(request),
        "Validate the existing EKS cluster and the separate aws-eks-native workload root, including its configured operator contract and target identity.");

    public override RuntimeAdapterStepPlan PlanInfrastructure(RuntimeAdapterRequest request) => Guidance(
        base.PlanInfrastructure(request),
        "Review the exact saved plan for the separate aws-eks-native workload root; its configured handoff remains unqualified until the approved executor seals actual post-apply state lineage.");

    public override RuntimeAdapterStepPlan ApplyInfrastructure(RuntimeAdapterRequest request) => Guidance(
        base.ApplyInfrastructure(request),
        "Only the separately approved exact-plan executor may apply aws-eks-native and seal actual post-apply lineage; this planner does not apply infrastructure.");

    public override RuntimeAdapterStepPlan PlanRelease(RuntimeAdapterRequest request) => Guidance(
        base.PlanRelease(request),
        "Use install_handoff and verify_install_handoff to bind the observed EKS workload to the current honua-kubernetes-deployment Server target; plan day-2 changes through plan_gitops_engine.");

    public override RuntimeAdapterStepPlan ApplyRelease(RuntimeAdapterRequest request) => Guidance(
        base.ApplyRelease(request),
        "Propose the day-2 change for the current honua-kubernetes-deployment Server target as a durable operation; a separate human approval and Server policy gate precede submission.");

    public override RuntimeAdapterStepPlan Verify(RuntimeAdapterRequest request) => Guidance(
        base.Verify(request),
        "Verify the current target and operation through verify_install_handoff and get_devops_operation_status, then check Server health and runtime observations.");

    public override RuntimeAdapterStepPlan Rollback(RuntimeAdapterRequest request) => Guidance(
        base.Rollback(request),
        "Inspect the Server operation outcome and propose a corrected revision for a forward fix. Only a qualified target may use its separately approved, server-sealed bounded recovery; no free-form rollback is implied.");

    public override RuntimeAdapterStepPlan DetectDrift(RuntimeAdapterRequest request) => Guidance(
        base.DetectDrift(request),
        "Compare the current registered EKS target and observed runtime state with the approved desired revision before proposing a change.");

    public override RuntimeAdapterStepPlan ExportActualState(RuntimeAdapterRequest request) => Guidance(
        base.ExportActualState(request),
        "Read the current registered EKS target and Server operation state for evidence; do not infer installation from a configured target alone.");

    private static RuntimeAdapterStepPlan Guidance(RuntimeAdapterStepPlan step, string summary)
        => step with { Summary = summary, SuggestedCommands = [] };
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
