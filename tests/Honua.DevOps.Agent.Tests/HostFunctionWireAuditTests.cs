using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Tests;

public sealed class HostFunctionWireAuditTests
{
    [Fact]
    public async Task TypedMutation_OriginalSdkWireAndMetadataArePreservedWithOneAudit()
    {
        OperationResponse response = new("succeeded", "synthetic", [], [], [], [],
            BackendSteps: [new("write", "/fixture", true, "fixture", "", true)]);
        await CheckEquivalent((Func<OperationResponse>)(() => response), mutated: true);
        await CheckEquivalent((Func<WireFixture>)(() => new()), mutated: false);
        await CheckEquivalent((Func<OperationResponse?>)(() => null), mutated: false);
        await CheckEquivalent((Action)(() => { }), mutated: false);
    }

    [Fact]
    public async Task InvocationCancellationReachesDelegateWithoutFabricatedAudit()
    {
        using CancellationTokenSource cts = new();
        CapturingSink sink = new();
        bool entered = false;
        AIFunction inner = (AIFunction)CapabilityToolset.CreateTool((Func<CancellationToken, Task<OperationResponse>>)(async token =>
        {
            entered = true;
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }), "fixture", "description");
        AuditingAIFunction wrapped = new(inner, Context(sink));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await wrapped.InvokeAsync(new AIFunctionArguments(), cts.Token));
        Assert.True(entered);
        Assert.Empty(sink.Records);
    }

    private static async Task CheckEquivalent(Delegate function, bool mutated)
    {
        AIFunction original = AIFunctionFactory.Create(function,
            new AIFunctionFactoryOptions { Name = "fixture", Description = "description" });
        AIFunction inner = (AIFunction)CapabilityToolset.CreateTool(function, "fixture", "description");
        CapturingSink sink = new();
        AuditingAIFunction wrapped = new(inner, Context(sink));
        Assert.Equal(original.Name, wrapped.Name);
        Assert.Equal(original.Description, wrapped.Description);
        Assert.Equal(original.JsonSchema.GetRawText(), wrapped.JsonSchema.GetRawText());
        Assert.Equal(original.ReturnJsonSchema?.GetRawText(), wrapped.ReturnJsonSchema?.GetRawText());
        object? expected = await original.InvokeAsync(new AIFunctionArguments());
        object? actual = await wrapped.InvokeAsync(new AIFunctionArguments());
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(mutated, Assert.Single(sink.Records).Mutated);
        Assert.DoesNotContain("UnmarshaledToolResult", JsonSerializer.Serialize(actual), StringComparison.Ordinal);
        Assert.DoesNotContain("BackendSteps", JsonSerializer.Serialize(actual), StringComparison.Ordinal);
    }

    private static AuditContext Context(IAuditSink sink) => new("session", "plan", "plan", "pr-first", "interactive", sink);
    private sealed class CapturingSink : IAuditSink
    {
        internal List<AuditRecord> Records { get; } = [];
        public string Target => "capture";
        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        { Records.Add(record); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed record WireFixture
    {
        [JsonPropertyName("namedValue")] public DayOfWeek Value { get; init; } = DayOfWeek.Monday;
        [JsonIgnore] public string Hidden { get; init; } = "private";
    }
}
