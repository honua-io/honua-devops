using System.ComponentModel;
using System.Diagnostics;

namespace Honua.DevOps.Agent.Operations;

internal sealed class SystemProvisioningProcessRunner : IProvisioningProcessRunner
{
    internal static SystemProvisioningProcessRunner Instance { get; } = new();

    private SystemProvisioningProcessRunner()
    {
    }

    /// <summary>
    /// Environment override for the Terraform binary, for hosts that do not put it on PATH
    /// (for example the MCP container, where it is mounted).
    /// </summary>
    internal const string TerraformExecutableVariable = "HONUA_DEVOPS_TERRAFORM_BIN";

    public bool CanRun(string fileName) => Resolve(fileName) is not null;

    // Locates an executable the same way the launcher will: an explicit override first,
    // then each PATH entry. Returns null when it is genuinely unavailable.
    internal static string? Resolve(string fileName, string? pathVariable = null)
    {
        if (string.Equals(fileName, "terraform", StringComparison.Ordinal)
            && Environment.GetEnvironmentVariable(TerraformExecutableVariable) is { } configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? fileName : null;
        }

        string[] executableNames = OperatingSystem.IsWindows()
            ? [fileName + ".exe", fileName]
            : [fileName];

        string search = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string executableName in executableNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim(), executableName);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is skipped rather than failing the lookup.
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public async Task<ProvisioningProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProvisioningProcessResult(-1, string.Empty, "Process did not start.", false);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new ProvisioningProcessResult(-1, string.Empty, exception.Message, false);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProvisioningProcessResult(
                -1,
                await ReadCompletedAsync(stdoutTask),
                await ReadCompletedAsync(stderrTask),
                true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProvisioningProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask,
            false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
    }

    private static async Task<string> ReadCompletedAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
