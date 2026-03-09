namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal abstract class RuntimeAdapterBase : IRuntimeAdapter
{
    public abstract RuntimeAdapterCapability Capability { get; }

    protected abstract string TerraformModule { get; }

    protected abstract string ReleaseBackend { get; }

    protected abstract string RollbackHandle { get; }

    protected virtual string TargetDisplayName => Capability.Target;

    public virtual RuntimeAdapterStepPlan Validate(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "validate",
            Summary: $"Validate runtime target `{TargetDisplayName}` against execution tier `{request.ExecutionTier.ToConfigValue()}` and Terraform source `{request.TerraformRepository}@{request.TerraformRef}`.",
            SuggestedCommands:
            [
                $"test -d {ShellQuote(Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform", "modules", TerraformModule))}",
                $"{request.GitOpsTool} validate-target --target {ShellQuote(Capability.Target)} --tier {ShellQuote(request.ExecutionTier.ToConfigValue())}"
            ],
            ValidationChecks:
            [
                $"target-capability:{Capability.Target}",
                $"execution-tier:{request.ExecutionTier.ToConfigValue()}"
            ],
            Risks:
            [
                $"Using `{Capability.Target}` without validated module inputs can create target-specific drift."
            ]);
    }

    public virtual RuntimeAdapterStepPlan PlanInfrastructure(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-infra",
            Summary: $"Plan Terraform-backed infrastructure changes for `{TargetDisplayName}`.",
            SuggestedCommands:
            [
                $"terraform -chdir {ShellQuote(Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform"))} plan -var target={ShellQuote(Capability.Target)} -var revision={ShellQuote(request.Revision)}"
            ],
            ValidationChecks:
            [
                $"infra-plan:{Capability.Target}"
            ],
            Risks:
            [
                "Infra plan can be stale if the validated Terraform ref changes before execution."
            ]);
    }

    public virtual RuntimeAdapterStepPlan ApplyInfrastructure(RuntimeAdapterRequest request)
    {
        string mode = request.DryRun ? "plan-only" : "write-enabled";
        return new RuntimeAdapterStepPlan(
            Operation: "apply-infra",
            Summary: $"Apply infrastructure changes for `{TargetDisplayName}` in {mode} mode.",
            SuggestedCommands:
            [
                request.DryRun
                    ? $"terraform -chdir {ShellQuote(Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform"))} plan -var target={ShellQuote(Capability.Target)}"
                    : $"terraform -chdir {ShellQuote(Path.Combine(request.TerraformLocalPath, "infrastructure", "terraform"))} apply -var target={ShellQuote(Capability.Target)}"
            ],
            ValidationChecks:
            [
                $"infra-apply-gate:{Capability.Target}"
            ],
            Risks:
            [
                "Applying infra before confirming release compatibility can widen rollback scope."
            ]);
    }

    public virtual RuntimeAdapterStepPlan PlanRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "plan-release",
            Summary: $"Plan release actions for `{TargetDisplayName}` via `{ReleaseBackend}`.",
            SuggestedCommands:
            [
                BuildReleasePlanCommand(request)
            ],
            ValidationChecks:
            [
                $"release-plan:{Capability.Target}"
            ],
            Risks:
            [
                "Release plans must stay aligned with the currently validated infra shape."
            ]);
    }

    public virtual RuntimeAdapterStepPlan ApplyRelease(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "apply-release",
            Summary: $"Apply release actions for `{TargetDisplayName}` with backend `{ReleaseBackend}`.",
            SuggestedCommands:
            [
                request.DryRun
                    ? BuildReleasePlanCommand(request)
                    : BuildReleaseApplyCommand(request)
            ],
            ValidationChecks:
            [
                $"release-apply-gate:{Capability.Target}"
            ],
            Risks:
            [
                "Release apply without post-deploy verification can hide target-specific regressions."
            ]);
    }

    public virtual RuntimeAdapterStepPlan Verify(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "verify",
            Summary: $"Verify rollout health for `{TargetDisplayName}` using the shared smoke contract and target-aware health checks.",
            SuggestedCommands:
            [
                "HONUA_SMOKE_BASE_URL=https://<endpoint> ./scripts/smoke-contract.sh",
                $"{request.GitOpsTool} verify --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)}"
            ],
            ValidationChecks:
            [
                $"adapter-verify:{Capability.Target}",
                $"rollback-ready:{Capability.Target}"
            ],
            Risks:
            [
                "Smoke checks alone may miss target-specific traffic or scaling regressions."
            ]);
    }

    public virtual RuntimeAdapterStepPlan Rollback(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "rollback",
            Summary: $"Rollback `{TargetDisplayName}` using `{RollbackHandle}` and the operator evidence bundle.",
            SuggestedCommands:
            [
                BuildRollbackCommand(request)
            ],
            ValidationChecks:
            [
                $"rollback-handle:{Capability.Target}"
            ],
            Risks:
            [
                "Rollback can fail if schema, config, and release state do not move together."
            ]);
    }

    public virtual RuntimeAdapterStepPlan DetectDrift(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "drift",
            Summary: $"Detect desired-vs-actual drift for `{TargetDisplayName}`.",
            SuggestedCommands:
            [
                $"{request.GitOpsTool} drift --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)}"
            ],
            ValidationChecks:
            [
                $"drift-report:{Capability.Target}"
            ],
            Risks:
            [
                "Silent drift can invalidate promotion assumptions and rollback safety."
            ]);
    }

    public virtual RuntimeAdapterStepPlan ExportActualState(RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterStepPlan(
            Operation: "export-actual-state",
            Summary: $"Export actual runtime state for `{TargetDisplayName}` to support evidence capture and reconciliation.",
            SuggestedCommands:
            [
                $"{request.GitOpsTool} export-actual --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)} --revision {ShellQuote(request.Revision)}"
            ],
            ValidationChecks:
            [
                $"actual-state-export:{Capability.Target}"
            ],
            Risks:
            [
                "Without actual-state export, drift and promotion decisions rely on incomplete state."
            ]);
    }

    protected virtual string BuildReleasePlanCommand(RuntimeAdapterRequest request)
    {
        return $"{request.GitOpsTool} plan-release --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)} --revision {ShellQuote(request.Revision)}";
    }

    protected virtual string BuildReleaseApplyCommand(RuntimeAdapterRequest request)
    {
        return $"{request.GitOpsTool} apply-release --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)} --revision {ShellQuote(request.Revision)}";
    }

    protected virtual string BuildRollbackCommand(RuntimeAdapterRequest request)
    {
        return $"{request.GitOpsTool} rollback --target {ShellQuote(Capability.Target)} --service {ShellQuote(request.Service)} --to-revision <known-good>";
    }

    protected static string ShellQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        bool safe = value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '-' or '_' or '.' or '/' or ':' or '@');
        if (safe)
        {
            return value;
        }

        return $"'{value.Replace("'", "'\"'\"'")}'";
    }
}
