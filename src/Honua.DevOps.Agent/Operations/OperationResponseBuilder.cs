using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.OrchestrationHost;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Operations;

internal sealed class OperationResponseBuilder
{
    private readonly List<string> _findings = [];
    private readonly List<string> _actions = [];
    private readonly List<string> _validationChecks = [];
    private readonly List<string> _risks = [];
    private List<OperationBackendStep>? _backendSteps;
    private string? _status;
    private string? _summary;
    private OperationEvidence? _evidence;
    private GitOpsPlan? _gitOpsPlan;
    private ReleaseOrchestrationPlan? _releaseOrchestration;
    private ServiceBundleReconciliationPlan? _serviceBundleReconciliation;
    private OrchestrationHostPlan? _orchestrationHost;

    internal OperationResponseBuilder Status(string value)
    {
        _status = value;
        return this;
    }

    internal OperationResponseBuilder Summary(string value)
    {
        _summary = value;
        return this;
    }

    internal OperationResponseBuilder WithBackend(BackendCallResult result, string label)
    {
        _findings.Add($"{label} endpoint: {result.Endpoint}");
        _findings.Add($"Backend result: {result.Detail}");
        _findings.Add($"Response excerpt: {result.PayloadPreview}");
        return this;
    }

    internal OperationResponseBuilder AddFinding(string value)
    {
        _findings.Add(value);
        return this;
    }

    internal OperationResponseBuilder AddFindings(IEnumerable<string> values)
    {
        _findings.AddRange(values);
        return this;
    }

    internal OperationResponseBuilder AddAction(string value)
    {
        _actions.Add(value);
        return this;
    }

    internal OperationResponseBuilder AddActions(IEnumerable<string> values)
    {
        _actions.AddRange(values);
        return this;
    }

    internal OperationResponseBuilder AddValidationCheck(string value)
    {
        _validationChecks.Add(value);
        return this;
    }

    internal OperationResponseBuilder AddValidationChecks(IEnumerable<string> values)
    {
        _validationChecks.AddRange(values);
        return this;
    }

    internal OperationResponseBuilder AddRisk(string value)
    {
        _risks.Add(value);
        return this;
    }

    internal OperationResponseBuilder AddRisks(IEnumerable<string> values)
    {
        _risks.AddRange(values);
        return this;
    }

    internal OperationResponseBuilder WithOperatorPolicy(OperatorPolicy.OperatorPolicy policy)
    {
        _actions.Add($"Operator policy approval mode: `{policy.ApprovalMode.ToConfigValue()}`.");
        _actions.Add($"Audit hook target: `{policy.AuditHookTarget}`.");
        _actions.Add($"Support session access: `{policy.SupportSession.Access.ToConfigValue()}` with TTL `{policy.SupportSession.TtlMinutes}` minutes and customer-visible `{policy.SupportSession.CustomerVisible}`.");
        if (policy.BreakGlassPostActionReviewRequired)
        {
            _actions.Add("Break-glass actions require post-action review.");
        }

        _validationChecks.Add("approval-policy");
        _validationChecks.Add("audit-hook");
        if (policy.SupportSession.Access != SupportSessionAccess.Disabled)
        {
            _validationChecks.Add("support-session-ttl");
        }

        if (policy.BreakGlassPostActionReviewRequired)
        {
            _validationChecks.Add("post-action-review");
        }

        if (policy.ApprovalMode == ApprovalMode.DirectAllowed)
        {
            _risks.Add("Direct execution policy increases the chance of bypassing review compared to PR-first posture.");
        }

        if (policy.SupportSession.Access != SupportSessionAccess.Disabled)
        {
            _risks.Add("Delegated support access must stay scoped and time-bound to avoid silent privilege expansion.");
        }

        return this;
    }

    internal OperationResponseBuilder WithEvidence(OperationEvidence evidence)
    {
        _evidence = evidence;
        return this;
    }

    internal OperationResponseBuilder WithGitOpsPlan(GitOpsPlan plan)
    {
        _gitOpsPlan = plan;
        return this;
    }

    internal OperationResponseBuilder WithReleaseOrchestration(ReleaseOrchestrationPlan plan)
    {
        _releaseOrchestration = plan;
        return this;
    }

    internal OperationResponseBuilder WithServiceBundleReconciliation(ServiceBundleReconciliationPlan plan)
    {
        _serviceBundleReconciliation = plan;
        return this;
    }

    internal OperationResponseBuilder WithOrchestrationHost(OrchestrationHostPlan plan)
    {
        _orchestrationHost = plan;
        return this;
    }

    internal OperationResponseBuilder AddBackendStep(OperationBackendStep step)
    {
        _backendSteps ??= [];
        _backendSteps.Add(step);
        return this;
    }

    internal OperationResponseBuilder AddBackendSteps(IEnumerable<OperationBackendStep> steps)
    {
        _backendSteps ??= [];
        _backendSteps.AddRange(steps);
        return this;
    }

    internal OperationResponse Build()
    {
        if (_status is null)
        {
            throw new InvalidOperationException("OperationResponseBuilder requires Status before Build.");
        }

        if (_summary is null)
        {
            throw new InvalidOperationException("OperationResponseBuilder requires Summary before Build.");
        }

        return new OperationResponse(
            Status: _status,
            Summary: _summary,
            Findings: _findings,
            Actions: _actions,
            ValidationChecks: _validationChecks,
            Risks: _risks,
            Evidence: _evidence,
            GitOpsPlan: _gitOpsPlan,
            ReleaseOrchestration: _releaseOrchestration,
            ServiceBundleReconciliation: _serviceBundleReconciliation,
            OrchestrationHost: _orchestrationHost,
            BackendSteps: _backendSteps);
    }
}
