using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.Deliverable;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Tests;

public class DeliverableLifecyclePlannerTests
{
    [Fact]
    public void Build_FromDraft_ProducesOrderedTransitionsWithEnvAndGatePerStep()
    {
        DeliverableLifecyclePlan plan = BuildPlan(
            state: DeliverableLifecycleState.Draft,
            edition: "enterprise",
            promotionPlan: BuildPromotionPlan());

        Assert.Equal(
            new[]
            {
                (DeliverableLifecycleState.Draft, DeliverableLifecycleState.Preview),
                (DeliverableLifecycleState.Preview, DeliverableLifecycleState.Approved),
                (DeliverableLifecycleState.Approved, DeliverableLifecycleState.Published)
            },
            plan.Transitions.Select(t => (t.FromState, t.ToState)).ToArray());

        DeliverableTransition draftToPreview = plan.Transitions[0];
        Assert.Equal("dev", draftToPreview.TargetEnvironment);
        Assert.Equal("lower-env-preview-rollout", draftToPreview.Gate);
        Assert.Equal("pro", draftToPreview.RequiredEdition);
        Assert.Contains("preview-link", draftToPreview.RequiredEvidence);

        DeliverableTransition approvedToPublished = plan.Transitions[2];
        Assert.Equal("prod", approvedToPublished.TargetEnvironment);
        Assert.Equal("enterprise", approvedToPublished.RequiredEdition);
    }

    [Fact]
    public void Build_PreviewToApproved_EmitsGovernedApprovalSuggestedAction()
    {
        DeliverableLifecyclePlan plan = BuildPlan(
            state: DeliverableLifecycleState.Draft,
            edition: "pro",
            promotionPlan: null);

        DeliverableTransition approvalStep = plan.Transitions.Single(t => t.ToState == DeliverableLifecycleState.Approved);
        Assert.Equal("steward-approval", approvalStep.Gate);
        Assert.Equal(ConsoleApprovalTrigger.SourceId, approvalStep.ApprovalSource);

        SuggestedAction? action = approvalStep.ApprovalAction;
        Assert.NotNull(action);
        Assert.True(action!.RequiresApproval);
        Assert.True(action.MutatesState);
        Assert.Equal("deliverable-approval", action.Kind);
        Assert.Equal(action, DeliverableLifecyclePlanner.FindApprovalAction(plan));
    }

    [Fact]
    public void Build_ApprovedToPublished_BindsToGatedPromotionWithProdEvidence()
    {
        ReleaseOrchestrationPlan promotionPlan = BuildPromotionPlan();
        DeliverableLifecyclePlan plan = BuildPlan(
            state: DeliverableLifecycleState.Approved,
            edition: "enterprise",
            promotionPlan: promotionPlan);

        DeliverableTransition publishStep = Assert.Single(plan.Transitions);
        Assert.Equal(DeliverableLifecycleState.Approved, publishStep.FromState);
        Assert.Equal(DeliverableLifecycleState.Published, publishStep.ToState);
        Assert.Same(promotionPlan, publishStep.PromotionPlan);
        Assert.Equal("gated-promotion", publishStep.PromotionMode);

        // Required evidence comes straight from the reused gated-promotion engine.
        Assert.Equal(promotionPlan.PromotionPolicy.RequiredEvidence, publishStep.RequiredEvidence);
        Assert.Contains("approval-record", publishStep.RequiredEvidence);
        Assert.Contains("lower-env-evidence", publishStep.RequiredEvidence);
        Assert.Contains("smoke-contract", publishStep.RequiredEvidence);
        Assert.Contains("slo-gate-evidence", publishStep.RequiredEvidence);
    }

    [Fact]
    public void Build_FromApproved_OnlyPlansPublishStep()
    {
        DeliverableLifecyclePlan plan = BuildPlan(
            state: DeliverableLifecycleState.Approved,
            edition: "enterprise",
            promotionPlan: BuildPromotionPlan());

        Assert.True(plan.CrossEnvironmentPromotionPlanned);
        Assert.Single(plan.Transitions);
    }

    private static DeliverableLifecyclePlan BuildPlan(
        DeliverableLifecycleState state,
        string edition,
        ReleaseOrchestrationPlan? promotionPlan)
    {
        Deliverable deliverable = new(
            DeliverableId: "GIS-42:map",
            WorkItemId: "GIS-42",
            Kind: "map",
            State: state,
            Environment: "dev",
            PreviewUrl: null,
            Provenance: []);

        return DeliverableLifecyclePlanner.Build(
            deliverable,
            lowerEnvironment: "dev",
            publishEnvironment: "prod",
            callerEdition: edition,
            approvalTrigger: new ConsoleApprovalTrigger(),
            promotionPlan: promotionPlan,
            approvalOperationId: "deliverable-lifecycle:GIS-42:map");
    }

    internal static ReleaseOrchestrationPlan BuildPromotionPlan()
    {
        RuntimeAdapterRequest request = new(
            Service: "GIS-42:map",
            Environments: ["dev", "prod"],
            Revision: "deliverable-artifact",
            Action: "promote",
            ChangeSummary: "publish deliverable",
            GitOpsTool: "honua-gitops",
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
            DryRun: true,
            ExecutionMode: Honua.DevOps.Agent.Operations.ExecutionMode.Plan,
            ExecutionTier: Honua.DevOps.Agent.Operations.ExecutionTier.Plan);

        IReadOnlyList<RuntimeAdapterWorkflow> workflows = RuntimeAdapterRegistry
            .ResolveMany(["eks"])
            .Select(adapter => adapter.BuildWorkflow(request))
            .ToArray();

        return ReleaseOrchestrationPlanner.Build(
            workflows,
            ["dev", "prod"],
            "promote",
            dryRun: true,
            "deliverable-publish-gated-promotion");
    }
}
