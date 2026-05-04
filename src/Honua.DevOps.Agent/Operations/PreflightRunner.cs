using Honua.DevOps.Agent.Operations.RuntimeAdapters;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations;

internal static class PreflightRunner
{
    internal static async Task<int> RunAsync(
        OperationRuntime runtime,
        OperatorPolicyModel? policy,
        BackendConfiguration backendConfiguration,
        BackendGateway backendGateway,
        CancellationToken cancellationToken)
    {
        OperatorPolicyModel effectivePolicy = policy ?? OperatorPolicyModel.Default;
        Console.WriteLine("Running honua-devops preflight...");
        Console.WriteLine($"Honua API base: {backendConfiguration.HonuaApiBaseUri}");
        Console.WriteLine($"OTEL base: {backendConfiguration.OTelBaseUri}");
        Console.WriteLine($"Honua readiness path: /{backendConfiguration.HonuaReadinessPath}");
        Console.WriteLine($"Honua manifest apply path: /{backendConfiguration.HonuaManifestApplyPath}");
        Console.WriteLine("Honua auth header: X-API-Key");
        Console.WriteLine($"GitOps mode: {runtime.GitOpsTool}");
        Console.WriteLine($"Approval mode: {effectivePolicy.ApprovalMode.ToConfigValue()}");
        Console.WriteLine($"Audit hook target: {effectivePolicy.AuditHookTarget}");
        Console.WriteLine($"Support session: {effectivePolicy.SupportSession.Access.ToConfigValue()} ttl={effectivePolicy.SupportSession.TtlMinutes}m visible={effectivePolicy.SupportSession.CustomerVisible}");
        Console.WriteLine($"Terraform repo/ref: {runtime.TerraformRepository}@{runtime.TerraformRef}");
        Console.WriteLine($"Terraform local path: {runtime.TerraformLocalPath}");
        Console.WriteLine($"Terraform targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}");
        Console.WriteLine($"Deploy-control target: {runtime.DeployTargetId ?? "not configured"}");
        IReadOnlyList<IRuntimeAdapter> adapters = RuntimeAdapterRegistry.ResolveMany(runtime.TerraformDeploymentTargets);
        Console.WriteLine($"Runtime adapters: {string.Join(" | ", adapters.Select(adapter => adapter.Capability.ToSummary()))}");
        RuntimeAdapterRequest adapterRequest = new(
            Service: "honua-platform",
            Environments: ["dev"],
            Revision: "HEAD",
            Action: "plan",
            ChangeSummary: "preflight validation",
            GitOpsTool: runtime.GitOpsTool,
            TerraformRepository: runtime.TerraformRepository,
            TerraformRef: runtime.TerraformRef,
            TerraformLocalPath: runtime.TerraformLocalPath,
            DryRun: true,
            ExecutionMode: runtime.ExecutionMode,
            ExecutionTier: runtime.ExecutionTier);
        Console.WriteLine($"Adapter validate plans: {string.Join(" | ", adapters.Select(adapter => adapter.Validate(adapterRequest).Summary))}");

        bool terraformPathOk = Directory.Exists(runtime.TerraformLocalPath);
        PrintCheck(
            terraformPathOk,
            "Terraform local path",
            terraformPathOk ? "path exists" : "path not found; target discovery may be stale");

        BackendCallResult honuaProbe = await backendGateway.ProbeHonuaAsync(cancellationToken);
        PrintCheck(honuaProbe.IsSuccess, "Honua API probe", $"{honuaProbe.Detail} ({honuaProbe.Endpoint})");

        BackendCallResult otelProbe = await backendGateway.ProbeOtelAsync(cancellationToken);
        PrintCheck(otelProbe.IsSuccess, "OTEL probe", $"{otelProbe.Detail} ({otelProbe.Endpoint})");

        BackendCallResult? deployPreflight = null;
        if (!string.IsNullOrWhiteSpace(runtime.DeployTargetId))
        {
            deployPreflight = await backendGateway.RequestDeployPreflightAsync(includeDiagnostics: true, cancellationToken);
            PrintCheck(
                deployPreflight.IsSuccess,
                "Honua deploy preflight",
                $"{deployPreflight.Detail} ({deployPreflight.Endpoint})");
        }

        bool targetsOk = runtime.TerraformDeploymentTargets.Length > 0;
        PrintCheck(
            targetsOk,
            "Deployment targets",
            targetsOk ? "target set loaded" : "no deployment targets configured");
        bool adaptersOk = adapters.Count == runtime.TerraformDeploymentTargets.Length;
        PrintCheck(
            adaptersOk,
            "Runtime adapter catalog",
            adaptersOk ? "all targets resolved to adapter capabilities" : "one or more targets failed adapter resolution");

        bool deployPreflightOk = deployPreflight?.IsSuccess ?? true;
        bool success = terraformPathOk && honuaProbe.IsSuccess && otelProbe.IsSuccess && deployPreflightOk && targetsOk && adaptersOk;
        if (success)
        {
            Console.WriteLine("Preflight passed.");
            return 0;
        }

        Console.Error.WriteLine("Preflight failed. Fix failing checks and retry.");
        return 2;
    }

    private static void PrintCheck(bool success, string label, string detail)
    {
        string status = success ? "OK" : "FAIL";
        Console.WriteLine($"[{status}] {label}: {detail}");
    }
}
