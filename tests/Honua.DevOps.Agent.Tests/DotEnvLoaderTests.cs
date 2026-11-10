using Honua.DevOps.Agent.Configuration;

namespace Honua.DevOps.Agent.Tests;

public sealed class DotEnvLoaderTests
{
    [Fact]
    public void LoadFromDirectory_LoadsDotEnvAndLocalOverridesWithoutOverwritingProcessEnvironment()
    {
        using TestEnvironmentVariableScope environment = new();
        using WorkingDirectoryScope workingDirectory = WorkingDirectoryScope.CreateTemporary();

        environment.Set("PROCESS_PRIORITY", "from-process");

        File.WriteAllText(
            Path.Combine(workingDirectory.Path, ".env"),
            """
            HONUA_DEVOPS_PROVIDER=codex
            SHARED_VALUE=from-dotenv
            PROCESS_PRIORITY=from-dotenv
            QUOTED_VALUE="value with spaces"
            EXPORTED_HASH=abc#123
            INLINE_COMMENT=value # ignored
            export EXPORTED_FLAG=true
            """);

        File.WriteAllText(
            Path.Combine(workingDirectory.Path, ".env.local"),
            """
            HONUA_DEVOPS_PROVIDER=claude
            SHARED_VALUE=from-local
            MULTILINE_VALUE="line-1\nline-2"
            """);

        IReadOnlyList<string> loadedFiles = DotEnvLoader.LoadFromDirectory(workingDirectory.Path);

        Assert.Equal(2, loadedFiles.Count);
        Assert.Equal("claude", Environment.GetEnvironmentVariable("HONUA_DEVOPS_PROVIDER"));
        // Unknown workspace keys are parsed but never imported into the process.
        foreach (string key in new[] { "SHARED_VALUE", "QUOTED_VALUE", "EXPORTED_HASH", "INLINE_COMMENT", "EXPORTED_FLAG", "MULTILINE_VALUE" })
            Assert.Null(Environment.GetEnvironmentVariable(key));
        Assert.Equal("from-process", Environment.GetEnvironmentVariable("PROCESS_PRIORITY"));
        var parsed = DotEnvLoader.ParseFile(Path.Combine(workingDirectory.Path, ".env"))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("value with spaces", parsed["QUOTED_VALUE"]);
        Assert.Equal("abc#123", parsed["EXPORTED_HASH"]);
        Assert.Equal("value", parsed["INLINE_COMMENT"]);
        Assert.Equal("true", parsed["EXPORTED_FLAG"]);
    }

    [Fact]
    public void ParseFile_RejectsMalformedEntry()
    {
        using WorkingDirectoryScope workingDirectory = WorkingDirectoryScope.CreateTemporary();

        string path = Path.Combine(workingDirectory.Path, ".env");
        File.WriteAllText(path, "NOT_A_VALID_LINE");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => DotEnvLoader.ParseFile(path));

        Assert.Contains("KEY=VALUE", exception.Message, StringComparison.Ordinal);
    }

    private sealed class WorkingDirectoryScope : IDisposable
    {
        private readonly string originalDirectory;

        internal string Path { get; }

        private WorkingDirectoryScope(string path)
        {
            originalDirectory = Directory.GetCurrentDirectory();
            Path = path;
            Directory.SetCurrentDirectory(path);
        }

        internal static WorkingDirectoryScope CreateTemporary()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"honua-devops-dotenv-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(path);
            return new WorkingDirectoryScope(path);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
