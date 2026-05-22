using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Providers;
using Microsoft.Agents.AI;
using OpenAI;

namespace Honua.DevOps.Agent.Tests;

public class LocalLlamaProviderSmokeTests
{
    private const string FixtureRelativePath = "fixtures/nim-chat-completion.json";

    [Fact]
    public async Task LocalLlamaSession_RoutesThroughConfiguredEndpoint_WithBearerAndModel()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_MODEL", "meta/llama-3.3-70b-instruct");
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_API_KEY", "nim-test-key");
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", "https://nim.test/v1");

        string fixtureBody = await ReadFixtureAsync(FixtureRelativePath);
        using TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixtureBody, Encoding.UTF8, "application/json")
        });
        using HttpClient httpClient = new(handler);
        HttpClientPipelineTransport transport = new(httpClient);
        OpenAIClientOptions clientOptions = new()
        {
            Transport = transport
        };

        ChatClientAgent agent = AgentProviderFactory.Create(
            ProviderKind.LocalLlama,
            systemPrompt: "you are a Honua operator stub",
            tools: null,
            clientOptionsOverride: clientOptions);

        AgentResponse response = await agent.RunAsync("describe the environment", session: null, options: null, cancellationToken: CancellationToken.None);

        Assert.NotNull(response);
        Assert.Single(handler.CapturedRequests);
        CapturedRequest captured = handler.CapturedRequests[0];

        Assert.Equal("POST", captured.Method);
        Assert.StartsWith("https://nim.test/v1/chat/completions", captured.Uri, StringComparison.Ordinal);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("nim-test-key", captured.AuthorizationParameter);
        Assert.NotNull(captured.Body);
        Assert.Contains("\"model\":\"meta/llama-3.3-70b-instruct\"", captured.Body, StringComparison.Ordinal);
        Assert.Contains("describe the environment", captured.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalLlamaSession_EmitsAuditRecordWithKebabProvider_AndCodexShapedFields()
    {
        AuditRecord codexRecord = NewRecord(provider: "codex");
        AuditRecord localLlamaRecord = NewRecord(provider: "local-llama");

        string codexLine = await SerializeViaSinkAsync(codexRecord);
        string localLlamaLine = await SerializeViaSinkAsync(localLlamaRecord);

        using JsonDocument codexDocument = JsonDocument.Parse(codexLine);
        using JsonDocument localLlamaDocument = JsonDocument.Parse(localLlamaLine);

        HashSet<string> codexProperties = ExtractPropertyNames(codexDocument.RootElement);
        HashSet<string> localLlamaProperties = ExtractPropertyNames(localLlamaDocument.RootElement);

        Assert.Equal(codexProperties, localLlamaProperties);
        Assert.Equal("local-llama", localLlamaDocument.RootElement.GetProperty("Provider").GetString());
        Assert.Equal("codex", codexDocument.RootElement.GetProperty("Provider").GetString());
    }

    private static async Task<string> SerializeViaSinkAsync(AuditRecord record)
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-audit-shape-{Guid.NewGuid():n}.jsonl");
        try
        {
            await using (IAuditSink sink = JsonlAuditSink.ForFile(path))
            {
                await sink.WriteAsync(record);
            }

            string line = (await File.ReadAllLinesAsync(path)).Single();
            return line;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static HashSet<string> ExtractPropertyNames(JsonElement element)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return names;
        }
        foreach (JsonProperty property in element.EnumerateObject())
        {
            names.Add(property.Name);
        }
        return names;
    }

    private static AuditRecord NewRecord(string provider)
    {
        return new AuditRecord(
            Timestamp: DateTimeOffset.UnixEpoch,
            SessionId: "session",
            OperationId: "operation",
            ToolName: "describe_environment",
            Arguments: new Dictionary<string, string> { ["service"] = "roads-api" },
            Status: "environment-described",
            Summary: "summary",
            Mutated: false,
            ExecutionMode: "plan",
            ExecutionTier: "plan",
            ApprovalMode: "pr-first",
            Provider: provider,
            BackendSteps: null,
            Evidence: null);
    }

    private static async Task<string> ReadFixtureAsync(string relativePath)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        return await File.ReadAllTextAsync(fullPath);
    }
}
