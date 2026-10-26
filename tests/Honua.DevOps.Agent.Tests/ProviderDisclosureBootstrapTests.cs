using System.Text.Json;
using System.Diagnostics;
using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations.Eval;
using Honua.DevOps.Agent.Operations.Troubleshooting;
using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Tests;

public class ProviderDisclosureBootstrapTests
{
    private static readonly ProviderRoute Remote = new(ProviderKind.Codex, "fake-model", new Uri("https://provider.test/v1"), null);
    private static string Policy => JsonSerializer.Serialize(new
    {
        workloadDataClassification = "unclassified",
        approvals = new[] { new { provider = "codex", kind = "openai", destination = Remote.Destination,
            model = Remote.Model, useCase = "devops-operator", dataClassification = "unclassified" } }
    });

    [Theory]
    [InlineData("HONUA_DEVOPS_AI_DISCLOSURE_POLICY")]
    [InlineData("honua_devops_ai_disclosure_policy")]
    [InlineData("Honua_DevOps_Ai_Disclosure_Policy")]
    public void ActualBootstrap_RefusesDotEnvAndLocalFileApprovalOnEveryPlatform(string key)
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, null);
        environment.Set(key, null);
        environment.Set("HONUA_DEVOPS_TEST_DOTENV_COMPAT", null);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, ".env"), $"{key}='{Policy}'\nHONUA_DEVOPS_TEST_DOTENV_COMPAT=still-loaded\n");
        File.WriteAllText(Path.Combine(directory.Path, ".env.local"), $"{key}='{Policy}'\n");
        ProviderDisclosureSnapshot snapshot = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(directory.Path);
        Assert.False(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
        Assert.Null(Environment.GetEnvironmentVariable(key));
        Assert.Equal("still-loaded", Environment.GetEnvironmentVariable("HONUA_DEVOPS_TEST_DOTENV_COMPAT"));
    }

    [Fact]
    public void ActualBootstrap_PinsTrustedProcessApprovalBeforeConflictingFilesAndLaterEnvironmentChanges()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, Policy);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, ".env"), $"{ProviderDisclosureSnapshot.EnvironmentVariable}='{{}}'\n");
        File.WriteAllText(Path.Combine(directory.Path, ".env.local"), $"{ProviderDisclosureSnapshot.EnvironmentVariable}='invalid'\n");
        ProviderDisclosureSnapshot snapshot = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(directory.Path);
        Assert.True(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, "invalid later value");
        Assert.True(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Fact]
    public void ActualBootstrap_AbsentApprovalCannotBeGrantedByLaterProcessMutation()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, null);
        using TempDirectory directory = new();
        var snapshot = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(directory.Path);
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, Policy);
        Assert.False(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Fact]
    public void ActualBootstrap_DoesNotFindApprovalInParentDirectories()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, null);
        environment.Set("HONUA_DEVOPS_TEST_PARENT_ENV", null);
        using TempDirectory directory = new();
        string child = System.IO.Path.Combine(directory.Path, "project");
        Directory.CreateDirectory(child);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, ".env"),
            $"{ProviderDisclosureSnapshot.EnvironmentVariable}='{Policy}'\nHONUA_DEVOPS_TEST_PARENT_ENV=not-loaded\n");
        var snapshot = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(child);
        Assert.False(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
        Assert.Null(Environment.GetEnvironmentVariable("HONUA_DEVOPS_TEST_PARENT_ENV"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fake invalid policy canary")]
    public void ActualBootstrap_DoesNotRepairInvalidProcessPolicyFromDotEnv(string processPolicy)
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, processPolicy);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, ".env"), $"{ProviderDisclosureSnapshot.EnvironmentVariable}='{Policy}'\n");
        ProviderDisclosureSnapshot snapshot = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(directory.Path);
        Assert.False(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--mcp")]
    [InlineData("--list-tools")]
    [InlineData("--preflight")]
    public void NonModelBootstrapAndCliParsing_DoNotRequireOrParseApproval(string mode)
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set(ProviderDisclosureSnapshot.EnvironmentVariable, "malformed policy canary");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", null);
        using TempDirectory directory = new();
        _ = ProviderDisclosureSnapshot.CaptureAndLoadDotEnv(directory.Path);
        CliOptions options = CliOptions.Parse([mode]);
        Assert.True(mode switch
        {
            "--help" => options.Help, "--mcp" => options.Mcp,
            "--list-tools" => options.ListTools, "--preflight" => options.Preflight, _ => false
        });
    }

    [Fact]
    public async Task FixtureEval_RunsWithMalformedPolicyAndNoProviderCredentials()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", null);
        using TempDirectory directory = new();
        string root = FindRepositoryRoot();
        string fixture = Path.Combine(root, "eval/fixtures/blind-eval/golden-answers.json");
        var options = new BlindEvalCliOptions("smoke", EvaluationMode.ReadOnly,
            Path.Combine(directory.Path, "scorecard.json"), new string('0', 40), 0.8, fixture);
        using StringWriter output = new();
        int result = await BlindEvalMode.RunAsync(ProviderKind.Codex, options,
            new ProviderDisclosureSnapshot("malformed fake policy"), output, CancellationToken.None);
        Assert.Equal(BlindEvalRunner.ExitPassed, result);
        Assert.True(File.Exists(options.OutputPath));
    }

    [Theory]
    [InlineData("--help", "Usage:")]
    [InlineData("--list-tools", "operator tools:")]
    public async Task RealCliNonModelModes_WorkWithMalformedPolicyAndNoCredentials(string mode, string expectedOutput)
    {
        using TempDirectory directory = new();
        string output = await RunCliAsync(directory.Path, mode);
        Assert.Contains(expectedOutput, output);
    }

    [Fact]
    public async Task RealCliFixtureEval_WorksWithMalformedPolicyAndNoCredentials()
    {
        using TempDirectory directory = new();
        string scorecard = Path.Combine(directory.Path, "scorecard.json");
        await RunCliAsync(directory.Path, "--eval-blind", "--eval-fixture",
            Path.Combine(FindRepositoryRoot(), "eval/fixtures/blind-eval/golden-answers.json"),
            "--eval-output", scorecard, "--eval-commit", new string('0', 40));
        Assert.True(File.Exists(scorecard));
    }

    private static async Task<string> RunCliAsync(string directory, params string[] arguments)
    {
        string assembly = Path.Combine(AppContext.BaseDirectory, "Honua.DevOps.Agent.dll");
        Assert.True(File.Exists(assembly));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(assembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[ProviderDisclosureSnapshot.EnvironmentVariable] = "malformed synthetic policy canary";
        start.Environment["HONUA_DEVOPS_PROVIDER"] = "codex";
        start.Environment["HONUA_DEVOPS_EXECUTION_MODE"] = "plan";
        start.Environment["HONUA_DEVOPS_EXECUTION_TIER"] = "plan";
        start.Environment["HONUA_DEVOPS_APPROVAL_MODE"] = "pr-first";
        start.Environment["HONUA_DEVOPS_AUDIT_HOOK_TARGET"] = "stdout-evidence";
        foreach (string provider in new[] { "CODEX", "CLAUDE", "LOCAL_LLAMA", "BEDROCK" })
            start.Environment.Remove($"HONUA_DEVOPS_{provider}_API_KEY");
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        string stdout = await output;
        string stderr = await error;
        Assert.True(child.ExitCode == 0, $"CLI exited {child.ExitCode}: {stderr}");
        Assert.DoesNotContain("malformed synthetic policy canary", stdout + stderr);
        return stdout;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Honua.DevOps.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository fixture root not found.");
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "honua-disclosure-" + Guid.NewGuid().ToString("N"));
        internal TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
