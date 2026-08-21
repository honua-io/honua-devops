using System.ComponentModel;
using System.Diagnostics;

namespace Honua.DevOps.Agent.Operations;

internal sealed class SystemProvisioningProcessRunner : IProvisioningProcessRunner
{
    internal static SystemProvisioningProcessRunner Instance { get; } = new();

    private SystemProvisioningProcessRunner()
    {
    }

    public async Task<ProvisioningProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
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
