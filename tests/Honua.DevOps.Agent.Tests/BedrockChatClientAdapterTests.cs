using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.Documents;
using Honua.DevOps.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// Offline tests for the Bedrock Converse adapter's message/tool mapping. A capturing fake
/// <see cref="IAmazonBedrockRuntime"/> records the outbound <see cref="ConverseRequest"/> and
/// returns a canned <see cref="ConverseResponse"/>, so the full
/// Microsoft.Extensions.AI ⇄ Bedrock Converse round-trip is verified without network access.
/// </summary>
public class BedrockChatClientAdapterTests
{
    [Fact]
    public async Task GetResponseAsync_MapsSystemUserMessages_AndModelId()
    {
        ConverseResponse canned = TextResponse("hello from bedrock");
        CapturingBedrockRuntime runtime = new(canned);
        IChatClient client = new BedrockChatClientAdapter(runtime, "anthropic.claude-sonnet-4-20250514-v1:0", ownsRuntime: false);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, "you are a Honua operator"),
            new(ChatRole.User, "describe the environment")
        ];

        ChatResponse response = await client.GetResponseAsync(messages);

        ConverseRequest request = runtime.LastConverseRequest!;
        Assert.Equal("anthropic.claude-sonnet-4-20250514-v1:0", request.ModelId);

        // System prompt is carried in the System collection, not in Messages.
        Assert.NotNull(request.System);
        Assert.Single(request.System);
        Assert.Equal("you are a Honua operator", request.System[0].Text);

        // Only the user turn becomes a Converse message, mapped to the user role.
        Assert.Single(request.Messages);
        Assert.Equal(ConversationRole.User, request.Messages[0].Role);
        Assert.Equal("describe the environment", request.Messages[0].Content[0].Text);

        Assert.Equal("hello from bedrock", response.Text);
        Assert.Equal(ChatRole.Assistant, response.Messages[0].Role);
    }

    [Fact]
    public async Task GetResponseAsync_MapsToolDefinitionsIntoToolConfiguration()
    {
        CapturingBedrockRuntime runtime = new(TextResponse("ok"));
        IChatClient client = new BedrockChatClientAdapter(runtime, "model-x", ownsRuntime: false);

        AIFunction tool = AIFunctionFactory.Create(
            (string service) => $"described {service}",
            name: "describe_environment",
            description: "Describe a Honua environment");

        ChatOptions options = new()
        {
            Tools = [tool],
            MaxOutputTokens = 1024,
            Temperature = 0.2f
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], options);

        ConverseRequest request = runtime.LastConverseRequest!;

        Assert.NotNull(request.ToolConfig);
        Assert.Single(request.ToolConfig.Tools);
        ToolSpecification spec = request.ToolConfig.Tools[0].ToolSpec;
        Assert.Equal("describe_environment", spec.Name);
        Assert.Equal("Describe a Honua environment", spec.Description);

        // The JSON Schema is converted into a Bedrock Document object graph.
        Assert.NotNull(spec.InputSchema?.Json);
        Document schema = spec.InputSchema.Json;
        Assert.Equal(DocumentType.Dictionary, schema.Type);
        Assert.True(schema.AsDictionary().ContainsKey("type"));

        // Inference config is propagated.
        Assert.NotNull(request.InferenceConfig);
        Assert.Equal(1024, request.InferenceConfig.MaxTokens);
        Assert.Equal(0.2f, request.InferenceConfig.Temperature);
    }

    [Fact]
    public async Task GetResponseAsync_MapsToolUseResponseToFunctionCallContent()
    {
        ConverseResponse canned = new()
        {
            StopReason = StopReason.Tool_use,
            Output = new ConverseOutput
            {
                Message = new Message
                {
                    Role = ConversationRole.Assistant,
                    Content =
                    [
                        new ContentBlock { Text = "calling a tool" },
                        new ContentBlock
                        {
                            ToolUse = new ToolUseBlock
                            {
                                ToolUseId = "tool-123",
                                Name = "describe_environment",
                                Input = new Document(new Dictionary<string, Document>
                                {
                                    ["service"] = new Document("roads-api")
                                })
                            }
                        }
                    ]
                }
            },
            Usage = new TokenUsage { InputTokens = 11, OutputTokens = 7, TotalTokens = 18 }
        };

        CapturingBedrockRuntime runtime = new(canned);
        IChatClient client = new BedrockChatClientAdapter(runtime, "model-x", ownsRuntime: false);

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);

        FunctionCallContent call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("tool-123", call.CallId);
        Assert.Equal("describe_environment", call.Name);
        Assert.NotNull(call.Arguments);
        Assert.Equal("roads-api", Assert.IsType<string>(call.Arguments!["service"]));

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.NotNull(response.Usage);
        Assert.Equal(11, response.Usage!.InputTokenCount);
        Assert.Equal(7, response.Usage.OutputTokenCount);
        Assert.Equal(18, response.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task GetResponseAsync_MapsAssistantToolCallAndToolResultRoundTrip()
    {
        CapturingBedrockRuntime runtime = new(TextResponse("done"));
        IChatClient client = new BedrockChatClientAdapter(runtime, "model-x", ownsRuntime: false);

        // Assistant turn requesting a tool, followed by the tool result fed back in.
        ChatMessage assistantTurn = new(ChatRole.Assistant,
        [
            new FunctionCallContent("tool-9", "describe_environment", new Dictionary<string, object?> { ["service"] = "roads-api" })
        ]);
        ChatMessage toolResult = new(ChatRole.Tool,
        [
            new FunctionResultContent("tool-9", "environment described")
        ]);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "describe roads-api"),
            assistantTurn,
            toolResult
        ]);

        ConverseRequest request = runtime.LastConverseRequest!;
        Assert.Equal(3, request.Messages.Count);

        // Assistant ToolUse block.
        Message assistant = request.Messages[1];
        Assert.Equal(ConversationRole.Assistant, assistant.Role);
        ToolUseBlock toolUse = assistant.Content[0].ToolUse;
        Assert.Equal("tool-9", toolUse.ToolUseId);
        Assert.Equal("describe_environment", toolUse.Name);
        Assert.Equal("roads-api", toolUse.Input.AsDictionary()["service"].AsString());

        // Tool result is carried as a ToolResult block inside a user-role message.
        Message toolMessage = request.Messages[2];
        Assert.Equal(ConversationRole.User, toolMessage.Role);
        ToolResultBlock resultBlock = toolMessage.Content[0].ToolResult;
        Assert.Equal("tool-9", resultBlock.ToolUseId);
        Assert.Equal("environment described", resultBlock.Content[0].Text);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("content_filtered", "content_filter")]
    public async Task GetResponseAsync_MapsStopReasonToFinishReason(string bedrockStop, string expectedFinish)
    {
        ConverseResponse canned = TextResponse("x");
        canned.StopReason = StopReason.FindValue(bedrockStop);

        CapturingBedrockRuntime runtime = new(canned);
        IChatClient client = new BedrockChatClientAdapter(runtime, "model-x", ownsRuntime: false);

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);

        Assert.Equal(expectedFinish, response.FinishReason?.Value);
    }

    private static ConverseResponse TextResponse(string text)
    {
        return new ConverseResponse
        {
            StopReason = StopReason.End_turn,
            Output = new ConverseOutput
            {
                Message = new Message
                {
                    Role = ConversationRole.Assistant,
                    Content = [new ContentBlock { Text = text }]
                }
            },
            Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1, TotalTokens = 2 }
        };
    }

    /// <summary>
    /// Subclasses the concrete client (which never opens a connection at construction) and
    /// overrides only the Converse virtuals so tests stay fully offline.
    /// </summary>
    private sealed class CapturingBedrockRuntime : AmazonBedrockRuntimeClient
    {
        private readonly ConverseResponse _response;

        internal CapturingBedrockRuntime(ConverseResponse response)
            : base(new BasicAWSCredentials("test-access-key", "test-secret-key"),
                   new AmazonBedrockRuntimeConfig { RegionEndpoint = Amazon.RegionEndpoint.USWest2 })
        {
            _response = response;
        }

        internal ConverseRequest? LastConverseRequest { get; private set; }

        public override Task<ConverseResponse> ConverseAsync(
            ConverseRequest request,
            CancellationToken cancellationToken = default)
        {
            LastConverseRequest = request;
            return Task.FromResult(_response);
        }
    }
}
