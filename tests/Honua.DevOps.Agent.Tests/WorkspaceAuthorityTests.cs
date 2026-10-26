using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Providers;
using System.Text.Json;

namespace Honua.DevOps.Agent.Tests;

public sealed class WorkspaceAuthorityTests
{
    [Fact]
    public void DotEnv_CannotGrantExecutionOrInjectChildProcessEnvironment()
    {
        using TestEnvironmentVariableScope environment = new();
        string[] names = ["HONUA_DEVOPS_EXECUTION_MODE", "HONUA_DEVOPS_EXECUTION_TIER", "HONUA_DEVOPS_APPROVAL_MODE",
            "HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEYS", "HONUA_DEVOPS_TERRAFORM_LOCAL_PATH",
            "HONUA_DEVOPS_TERRAFORM_BIN",
            "HONUA_DEVOPS_HONUA_API_BASE_URL", "BASH_ENV", "PATH", "HONUA_DEVOPS_CODEX_RETRIES"];
        foreach (string name in names) environment.Set(name, null);
        string root = Directory.CreateTempSubdirectory("honua-workspace-authority-").FullName;
        try
        {
            File.WriteAllLines(Path.Combine(root, ".env"), names.Select(name => $"{name}=untrusted-workspace"));
            DotEnvLoader.LoadFromDirectory(root);
            foreach (string name in names) Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Runtime_RejectsRelativeSubstratePath()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_TERRAFORM_LOCAL_PATH", "../honua-iac");
        Assert.Throws<InvalidOperationException>(OperationRuntime.Load);
    }

    [Fact]
    public void Runtime_MissingSubstrateDoesNotInventWorkingDirectorySibling()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_TERRAFORM_LOCAL_PATH", null);
        Assert.Equal(string.Empty, OperationRuntime.Load().TerraformLocalPath);
    }
    [Theory]
    [InlineData("fake-model", "https://changed.test/v1")]
    [InlineData("changed-model", "https://approved.test/v1")]
    public void DotEnv_ModelInputsCannotExpandProcessDisclosureApproval(string model, string destination)
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", null);
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", null);
        environment.Set("HONUA_DEVOPS_AI_DISCLOSURE_POLICY", JsonSerializer.Serialize(new
        {
            workloadDataClassification = "unclassified",
            approvals = new[] { new { provider = "codex", kind = "openai", model = "fake-model",
                destination = "https://approved.test/v1", useCase = "devops-operator", dataClassification = "unclassified" } }
        }));
        string root = Directory.CreateTempSubdirectory("honua-model-route-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"),
                $"HONUA_DEVOPS_CODEX_MODEL={model}\nHONUA_DEVOPS_CODEX_ENDPOINT={destination}\nHONUA_DEVOPS_AI_DISCLOSURE_POLICY=untrusted\n");
            var snapshot = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(root);
            int credentialReads = 0, constructors = 0;
            var hooks = new ProviderConstructionHooks(route =>
            {
                credentialReads++;
                return new(route.Name, route.Model, "fake-key", route.Endpoint, route.Region);
            }, _ => { constructors++; throw new InvalidOperationException("unexpected construction"); });
            var failure = Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(
                ProviderKind.Codex, "synthetic", snapshot, ProviderModelUseCase.Operator, constructionHooks: hooks));
            Assert.Equal(ProviderDisclosureSnapshot.DenialMessage, failure.Message);
            Assert.Equal(0, credentialReads);
            Assert.Equal(0, constructors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DotEnv_ExactModelAllowlistPreservesPlatformCaseAndProcessPrecedence()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "trusted-process");
        environment.Set("honua_devops_claude_model", null);
        string root = Directory.CreateTempSubdirectory("honua-model-precedence-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"),
                "HONUA_DEVOPS_CODEX_MODEL=workspace\nhonua_devops_claude_model=case-model\n");
            DotEnvLoader.LoadFromDirectory(root);
            Assert.Equal("trusted-process", Environment.GetEnvironmentVariable("HONUA_DEVOPS_CODEX_MODEL"));
            Assert.Equal(OperatingSystem.IsWindows() ? "case-model" : null,
                Environment.GetEnvironmentVariable("honua_devops_claude_model"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
