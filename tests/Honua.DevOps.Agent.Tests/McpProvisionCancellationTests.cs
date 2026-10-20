using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace Honua.DevOps.Agent.Tests;

/// <summary>Production stdio MCP dispatch and System runner; no cloud, Terraform or approval mutation.</summary>
public sealed class McpProvisionCancellationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledPublicProvisionCall_StopsItsActualPlanProcessTree(bool traceProtocol)
    {
        if (!OperatingSystem.IsLinux()) return; // This control uses the Linux process birth/parent census.
        using TerraformTestRoot root = new();
        WriteOwnedPlanFixture(root, block: true);
        McpServerFixture server = new() { TerraformFixtureRoot = root.Path, TerraformFixtureExecutable = root.PlanScript, TraceOwnedProtocol = traceProtocol };
        await server.InitializeAsync();
        OwnedProcessIdentity[] owned = [];
        try
        {
            using CancellationTokenSource request = new();
            Task<CallToolResult> call = InvokeAsync();
            async Task<CallToolResult> InvokeAsync() => await server.Client.CallToolAsync("provision_infrastructure", Arguments("plan"), cancellationToken: request.Token);
            owned = await ObserveAdmittedProcessTreeAsync(root, call);
            request.Cancel(); // Client cancellation; cessation below must independently prove server-side cancellation.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            try
            {
                await WaitUntilAsync(() => owned.All(pid => !IsExecuting(pid)), "The public MCP cancellation did not stop the owned plan process tree.");
            }
            finally
            {
                output.WriteLine($"Public MCP traceProtocol={traceProtocol}, original/current process census: " + DescribeProcesses(owned));
                output.WriteLine("Owned MCP server stderr: " + string.Join("\n", server.CapturedOwnedServerErrors.Take(40)));
                output.WriteLine("Owned MCP SDK protocol trace: " + string.Join("\n", server.CapturedOwnedProtocolTrace));
            }
            Assert.False(File.Exists(Path.Combine(root.Path, "plan-completed")));
            // Cancelling one request must not terminate the server or cancel unrelated requests.
            IList<McpClientTool> tools = await server.Client.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "provision_infrastructure");
            Assert.DoesNotContain(server.CapturedBackendRequests, row => row.StartsWith("POST ", StringComparison.Ordinal));
        }
        finally
        {
            await StopOwnedFixtureProcessesAsync(owned);
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task UncancelledPublicProvisionCall_PreservesNormalChildResultAndPrivateTokenSchema()
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        WriteOwnedPlanFixture(root, block: false);
        McpServerFixture server = new() { TerraformFixtureRoot = root.Path, TerraformFixtureExecutable = root.PlanScript };
        await server.InitializeAsync();
        try
        {
            IList<McpClientTool> tools = await server.Client.ListToolsAsync();
            JsonElement schema = Assert.Single(tools, tool => tool.Name == "provision_infrastructure").JsonSchema;
            JsonElement properties = schema.GetProperty("properties");
            Assert.False(properties.TryGetProperty("cancellationToken", out _));
            Assert.True(properties.TryGetProperty("provisioningOperationId", out _));
            Assert.True(properties.TryGetProperty("idempotencyKey", out _));
            using CancellationTokenSource bound = new(TimeSpan.FromSeconds(30));
            CallToolResult result = await server.Client.CallToolAsync("provision_infrastructure", Arguments("plan"), cancellationToken: bound.Token);
            Assert.Equal("terraform-plan-failed", ResponseStatus(result));
            Assert.True(File.Exists(Path.Combine(root.Path, "plan-completed")));
            Assert.DoesNotContain(server.CapturedBackendRequests, row => row.StartsWith("POST ", StringComparison.Ordinal));
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    public async Task PublicApplyWithoutPlanOrApproval_StillStartsNoChild()
    {
        if (OperatingSystem.IsWindows()) return;
        using TerraformTestRoot root = new();
        WriteOwnedPlanFixture(root, block: true);
        McpServerFixture server = new() { TerraformFixtureRoot = root.Path, TerraformFixtureExecutable = root.PlanScript };
        await server.InitializeAsync();
        try
        {
            using CancellationTokenSource bound = new(TimeSpan.FromSeconds(30));
            CallToolResult result = await server.Client.CallToolAsync("provision_infrastructure", Arguments("apply"), cancellationToken: bound.Token);
            Assert.Equal("execution-mode-gated", ResponseStatus(result));
            Assert.False(File.Exists(Path.Combine(root.Path, "plan-parent.pid")));
            Assert.DoesNotContain(server.CapturedBackendRequests, row => row.StartsWith("POST ", StringComparison.Ordinal));
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    public async Task RegisteredProvisionFunction_ForwardsTokenToActualPlanProcessTree()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TerraformTestRoot root = new();
        WriteOwnedPlanFixture(root, block: true);
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        AIFunction function = Assert.Single(CapabilityToolset.Create(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway)
            .OfType<AIFunction>(), candidate => candidate.Name == "provision_infrastructure");
        // CanRun requires a present Terraform executable; the actual subprocess here
        // is the owned bash plan fixture. No Terraform binary or cloud action is invoked.
        string? previous = Environment.GetEnvironmentVariable(SystemProvisioningProcessRunner.TerraformExecutableVariable);
        Environment.SetEnvironmentVariable(SystemProvisioningProcessRunner.TerraformExecutableVariable, root.PlanScript);
        OwnedProcessIdentity[] owned = [];
        try
        {
            using CancellationTokenSource request = new();
            Task<object?> call = function.InvokeAsync(new AIFunctionArguments(Arguments("plan")), request.Token).AsTask();
            owned = await ObserveAdmittedProcessTreeAsync(root, call);
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            try
            {
                await WaitUntilAsync(() => owned.All(identity => !IsExecuting(identity)), "The registered function did not cancel its owned process tree.");
            }
            finally { output.WriteLine("Registered function original/current process census: " + DescribeProcesses(owned)); }
        }
        finally
        {
            await StopOwnedFixtureProcessesAsync(owned);
            Environment.SetEnvironmentVariable(SystemProvisioningProcessRunner.TerraformExecutableVariable, previous);
        }
    }

    [Fact]
    public async Task ExplicitPublicCancellation_StopsOnlyTheMatchingOwnedRequest()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TerraformTestRoot root = new();
        WriteOwnedPlanFixture(root, block: true);
        McpServerFixture server = new() { TerraformFixtureRoot = root.Path, TerraformFixtureExecutable = root.PlanScript, TraceOwnedProtocol = true };
        await server.InitializeAsync();
        OwnedProcessIdentity[] owned = [];
        try
        {
            RequestId requestId = new("owned-plan-" + Guid.NewGuid().ToString("n"));
            JsonRpcRequest request = new()
            {
                Id = requestId,
                Method = RequestMethods.ToolsCall,
                Params = JsonSerializer.SerializeToNode(new { name = "provision_infrastructure", arguments = Arguments("plan") })
            };
            // Explicit protocol request identity and awaited send isolate the server
            // from the pinned SDK's unproved client-token notification emission.
            Task<JsonRpcResponse> call = server.Client.SendRequestAsync(request);
            owned = await ObserveAdmittedProcessTreeAsync(root, call);
            await server.Client.SendMessageAsync(Cancellation(new RequestId("wrong-" + Guid.NewGuid().ToString("n"))));
            using CancellationTokenSource roundtrip = new(TimeSpan.FromSeconds(30));
            await server.Client.PingAsync(cancellationToken: roundtrip.Token);
            Assert.False(call.IsCompleted);
            Assert.All(owned, identity => Assert.True(IsExecuting(identity)));
            await server.Client.SendMessageAsync(Cancellation(requestId));
            try
            {
                await WaitUntilAsync(() => owned.All(identity => !IsExecuting(identity)), "The explicit matching public cancellation did not stop its owned process tree.");
            }
            finally
            {
                output.WriteLine("Explicit public original/current process census: " + DescribeProcesses(owned));
                output.WriteLine("Explicit public SDK protocol trace: " + string.Join("\n", server.CapturedOwnedProtocolTrace));
                output.WriteLine("Explicit public server stderr: " + string.Join("\n", server.CapturedOwnedServerErrors.Take(40)));
            }
            Assert.False(File.Exists(Path.Combine(root.Path, "plan-completed")));
            Assert.Contains(await server.Client.ListToolsAsync(cancellationToken: roundtrip.Token), tool => tool.Name == "provision_infrastructure");
            // Cancellation need not manufacture a particular local exception type.
            // The actual process cessation above is authoritative; observe completion
            // separately with a finite bound and retain its actual protocol shape.
            try
            {
                JsonRpcResponse response = await call.WaitAsync(TimeSpan.FromSeconds(5));
                string result = response.Result?.ToJsonString() ?? "null";
                output.WriteLine("Explicit cancelled request returned JSON-RPC result SHA256=" +
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(result))));
                Assert.DoesNotContain("terraform-plan-ready", result, StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is OperationCanceledException or McpException)
            {
                output.WriteLine("Explicit cancelled request terminated as " + exception.GetType().Name);
            }
        }
        finally
        {
            await StopOwnedFixtureProcessesAsync(owned);
            await server.DisposeAsync();
        }

        static JsonRpcNotification Cancellation(RequestId requestId) => new()
        {
            Method = NotificationMethods.CancelledNotification,
            Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = requestId })
        };
    }

    private static string DescribeProcesses(OwnedProcessIdentity[] identities) =>
        string.Join("; ", identities.Select(identity => $"original={identity}; current={ReadProcess(identity.Pid)?.ToString() ?? "absent"}"));

    private static Dictionary<string, object?> Arguments(string action) => new()
    {
        ["stack"] = "aws-ecs", ["size"] = "small", ["action"] = action,
        ["variablesJson"] = "{}", ["confirmed"] = action == "apply",
        ["confirmation"] = "", ["approvalReceiptJson"] = ""
    };

    private static string ResponseStatus(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
            return structured.TryGetProperty("status", out JsonElement status)
                ? status.GetString()! : structured.GetProperty("Status").GetString()!;
        string json = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("status", out JsonElement lower)
            ? lower.GetString()! : doc.RootElement.GetProperty("Status").GetString()!;
    }

    private static void WriteOwnedPlanFixture(TerraformTestRoot root, bool block)
    {
        File.WriteAllText(root.PlanScript, """
            #!/usr/bin/env bash
            set -euo pipefail
            owned="$(cd "$(dirname "$0")/.." && pwd)"
            printf '%s\n' "$$" > "$owned/plan-parent.pid"
            if [[ -f "$owned/block-plan" ]]; then
              sleep 60 &
              child=$!
              printf '%s\n' "$child" > "$owned/plan-child.pid"
              wait "$child"
            fi
            touch "$owned/plan-completed"
            printf '%s\n' 'owned local plan control exits nonzero; no Terraform/cloud action' >&2
            exit 7
            """);
        if (block) File.WriteAllText(Path.Combine(root.Path, "block-plan"), "owned local process control");
    }

    private static async Task<OwnedProcessIdentity[]> ObserveAdmittedProcessTreeAsync(TerraformTestRoot root, Task call)
    {
        string parent = Path.Combine(root.Path, "plan-parent.pid");
        string child = Path.Combine(root.Path, "plan-child.pid");
        await WaitUntilAsync(() => ReadOwnedPid(parent) is not null && ReadOwnedPid(child) is not null,
            "The real public provision call never admitted the owned plan fixture.", call);
        OwnedProcessIdentity parentIdentity = ReadProcess(ReadOwnedPid(parent)!.Value)!;
        OwnedProcessIdentity childIdentity = ReadProcess(ReadOwnedPid(child)!.Value)!;
        Assert.NotNull(parentIdentity);
        Assert.NotNull(childIdentity);
        string[] parentCommand = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{parentIdentity.Pid}/cmdline")).Split('\0');
        Assert.Contains(root.PlanScript, parentCommand);
        Assert.Equal(parentIdentity.Pid, childIdentity.ParentPid);
        OwnedProcessIdentity[] identities = [parentIdentity, childIdentity];
        Assert.All(identities, identity => Assert.True(IsExecuting(identity), "Admission must observe live actual child processes before cancellation."));
        return identities;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string error, Task? call = null)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (call?.IsCompleted == true) await call;
            await Task.Delay(25);
        }
        Assert.True(predicate(), error);
    }

    private static int? ReadOwnedPid(string path)
    {
        if (!File.Exists(path)) return null;
        string value = File.ReadAllText(path);
        return int.TryParse(value.Trim(), out int pid) && pid > 0 ? pid : null;
    }

    private sealed record OwnedProcessIdentity(int Pid, ulong StartTicks, int ParentPid, char State);

    private static OwnedProcessIdentity? ReadProcess(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // comm may contain spaces/parentheses; fields after its final ')' start at field3.
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(pid, ulong.Parse(fields[19]), int.Parse(fields[1]), fields[0][0]);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool IsExecuting(OwnedProcessIdentity expected)
    {
        OwnedProcessIdentity? current = ReadProcess(expected.Pid);
        return current is not null && current.StartTicks == expected.StartTicks && current.State is not ('Z' or 'X');
    }

    private static async Task StopOwnedFixtureProcessesAsync(OwnedProcessIdentity[] identities)
    {
        // A PID file alone grants no cleanup authority. The original process birth is
        // independently captured with the admitted parent/child relationship and checked again.
        foreach (OwnedProcessIdentity identity in identities)
        {
            if (!IsExecuting(identity)) continue;
            try
            {
                using Process process = Process.GetProcessById(identity.Pid);
                if (!IsExecuting(identity)) continue;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) when (!IsExecuting(identity)) { }
        }
    }

    [Fact]
    public async Task WrongProcessBirth_NeverCountsOrTerminatesTheLiveProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        using Process child = Process.Start(new ProcessStartInfo("sleep")
        {
            UseShellExecute = false, ArgumentList = { "30" }
        })!;
        OwnedProcessIdentity actual = ReadProcess(child.Id)!;
        try
        {
            Assert.True(IsExecuting(actual));
            OwnedProcessIdentity foreignBirth = actual with { StartTicks = checked(actual.StartTicks + 1) };
            Assert.False(IsExecuting(foreignBirth));
            await StopOwnedFixtureProcessesAsync([foreignBirth]);
            Assert.True(IsExecuting(actual));
            Assert.False(child.HasExited);
        }
        finally { await StopOwnedFixtureProcessesAsync([actual]); }
    }
}
