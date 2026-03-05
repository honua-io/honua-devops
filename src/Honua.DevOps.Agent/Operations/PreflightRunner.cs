namespace Honua.DevOps.Agent.Operations;

internal static class PreflightRunner
{
    internal static async Task<int> RunAsync(
        OperationRuntime runtime,
        BackendConfiguration backendConfiguration,
        BackendGateway backendGateway,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Running honua-devops preflight...");
        Console.WriteLine($"Honua API base: {backendConfiguration.HonuaApiBaseUri}");
        Console.WriteLine($"OTEL base: {backendConfiguration.OTelBaseUri}");
        Console.WriteLine($"Honua readiness path: /{backendConfiguration.HonuaReadinessPath}");
        Console.WriteLine($"Honua manifest apply path: /{backendConfiguration.HonuaManifestApplyPath}");
        Console.WriteLine("Honua auth header: X-API-Key");
        Console.WriteLine($"GitOps mode: {runtime.GitOpsTool}");
        Console.WriteLine($"Terraform repo/ref: {runtime.TerraformRepository}@{runtime.TerraformRef}");
        Console.WriteLine($"Terraform local path: {runtime.TerraformLocalPath}");
        Console.WriteLine($"Terraform targets: {string.Join(", ", runtime.TerraformDeploymentTargets)}");

        bool terraformPathOk = Directory.Exists(runtime.TerraformLocalPath);
        PrintCheck(
            terraformPathOk,
            "Terraform local path",
            terraformPathOk ? "path exists" : "path not found; target discovery may be stale");

        BackendCallResult honuaProbe = await backendGateway.ProbeHonuaAsync(cancellationToken);
        PrintCheck(honuaProbe.IsSuccess, "Honua API probe", $"{honuaProbe.Detail} ({honuaProbe.Endpoint})");

        BackendCallResult otelProbe = await backendGateway.ProbeOtelAsync(cancellationToken);
        PrintCheck(otelProbe.IsSuccess, "OTEL probe", $"{otelProbe.Detail} ({otelProbe.Endpoint})");

        bool targetsOk = runtime.TerraformDeploymentTargets.Length >= 6;
        PrintCheck(
            targetsOk,
            "Deployment targets",
            targetsOk ? "target set loaded" : "missing one or more expected targets");

        bool success = terraformPathOk && honuaProbe.IsSuccess && otelProbe.IsSuccess && targetsOk;
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
