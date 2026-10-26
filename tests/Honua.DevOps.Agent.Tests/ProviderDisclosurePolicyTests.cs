using System.Net;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.Eval;
using Honua.DevOps.Agent.Providers;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel.Primitives;

namespace Honua.DevOps.Agent.Tests;

public class ProviderDisclosurePolicyTests
{
    private static readonly ProviderRoute Remote = new(ProviderKind.Codex, "fake-model", new Uri("https://provider.test/v1"), null);

    [Theory]
    [InlineData("provider")]
    [InlineData("kind")]
    [InlineData("destination")]
    [InlineData("model")]
    [InlineData("useCase")]
    [InlineData("dataClassification")]
    public void EachApprovalFieldMustMatchExactly(string field)
    {
        var rule = Rule();
        rule[field] = field == "destination" ? "https://provider.test/v2" : "different";
        Assert.False(Snapshot(rule).IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Fact]
    public void ExactApproval_IsNotInterchangeableWithLiveEval()
    {
        var approved = Snapshot(Rule());
        Assert.True(approved.IsAllowed(Remote, ProviderModelUseCase.Operator));
        Assert.False(approved.IsAllowed(Remote, ProviderModelUseCase.BlindEval));
        Assert.True(ProviderDisclosureTestPolicy.Allow("bedrock", "fake-profile", "aws-bedrock:us-east-2", kind: "bedrock")
            .IsAllowed(new(ProviderKind.Bedrock, "fake-profile", null, "us-east-2"), ProviderModelUseCase.Operator));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"approvals\":null}")]
    [InlineData("{\"approvals\":[null]}")]
    [InlineData("{\"approvals\":[{}]}")]
    [InlineData("{\"unknown\":\"fake-policy-canary\"}")]
    [InlineData("{\"workloadDataClassification\":\"\"}")]
    public void AbsentOrMalformedPolicy_DeniesExternalWithoutEcho(string? policy)
    {
        var snapshot = new ProviderDisclosureSnapshot(policy);
        Assert.False(snapshot.IsAllowed(Remote, ProviderModelUseCase.Operator));
        var error = Assert.Throws<InvalidOperationException>(() => snapshot.RequireApproval(Remote, ProviderModelUseCase.Operator));
        Assert.Equal(ProviderDisclosureSnapshot.DenialMessage, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("https://provider.test/V1")]
    [InlineData("https://provider.test/v1/")]
    public void ApprovalKeepsPathCaseAndTrailingSlashDistinct(string destination)
    {
        var rule = Rule();
        rule["destination"] = destination;
        Assert.False(Snapshot(rule).IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Fact]
    public void UnknownRuleProperties_DenyInsteadOfBeingSilentlyIgnored()
    {
        var rule = Rule();
        rule["unknown"] = "fake-canary";
        Assert.False(Snapshot(rule).IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Fact]
    public void OversizedPolicyAndRuleCount_Deny()
    {
        Assert.False(new ProviderDisclosureSnapshot(new string(' ', 65537)).IsAllowed(Remote, ProviderModelUseCase.Operator));
        var json = JsonSerializer.Serialize(new { approvals = Enumerable.Repeat(Rule(), 129) });
        Assert.False(new ProviderDisclosureSnapshot(json).IsAllowed(Remote, ProviderModelUseCase.Operator));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("https://localhost/v1", true)]
    [InlineData("http://localhost.evil.test/v1", false)]
    [InlineData("http://127.0.0.1.evil.test/v1", false)]
    [InlineData("https://10.0.0.1/v1", false)]
    [InlineData("https://provider.test/v1", false)]
    [InlineData("https://user:fake@localhost/v1", false)]
    [InlineData("http://localhost/v1?fake=x", false)]
    [InlineData("http://localhost/v1#x", false)]
    public void LoopbackCompatibilityException_IsLiteralAndNonredirecting(string endpoint, bool allowed)
    {
        Assert.Equal(allowed, new ProviderDisclosureSnapshot(null).IsAllowed(
            Remote with { Endpoint = new Uri(endpoint) }, ProviderModelUseCase.Operator));
    }

    [Theory]
    [InlineData((int)ProviderKind.Codex)]
    [InlineData((int)ProviderKind.Bedrock)]
    public void DeniedFactory_NeverLoadsCredentialsOrConstructsBedrock(int providerValue)
    {
        var provider = (ProviderKind)providerValue;
        using TestEnvironmentVariableScope environment = new();
        environment.Set($"{provider.ToPrefix()}_MODEL", "fake-model");
        environment.Set($"{provider.ToPrefix()}_API_KEY", null);
        if (provider != ProviderKind.Bedrock) environment.Set($"{provider.ToPrefix()}_ENDPOINT", "https://provider.test/v1");
        else environment.Set("HONUA_DEVOPS_BEDROCK_REGION", "us-east-2");
        int credentialReads = 0, clientConstructions = 0;
        var hooks = new ProviderConstructionHooks(_ => { credentialReads++; throw new Exception("fake credential canary"); },
            _ => { clientConstructions++; throw new Exception("fake constructor canary"); });
        var error = Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(provider, "synthetic", new(null),
            ProviderModelUseCase.Operator, constructionHooks: hooks));
        Assert.Equal(ProviderDisclosureSnapshot.DenialMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, credentialReads);
        Assert.Equal(0, clientConstructions);
    }

    [Fact]
    public void ApprovalPrecedesRealMissingCredentialError_AndDefaultRouteIsExplicit()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "fake-model");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", null);
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", null);
        ProviderRoute route = ProviderConfiguration.LoadRoute(ProviderKind.Codex);
        Assert.Equal("https://api.openai.com/v1", route.Destination);
        var denied = Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(ProviderKind.Codex, "synthetic",
            new(null), ProviderModelUseCase.Operator));
        Assert.Equal(ProviderDisclosureSnapshot.DenialMessage, denied.Message);
        var allowed = ProviderDisclosureTestPolicy.Allow("codex", "fake-model", route.Destination);
        var missingKey = Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(ProviderKind.Codex, "synthetic",
            allowed, ProviderModelUseCase.Operator));
        Assert.Contains("HONUA_DEVOPS_CODEX_API_KEY", missingKey.Message);
    }

    [Fact]
    public void LiveEvalUsesItsOwnApprovalBeforeCredentialLoad()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "fake-model");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", null);
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", Remote.Destination);
        var operatorApproval = Snapshot(Rule());
        var denied = Assert.Throws<InvalidOperationException>(() => AgentBlindEvalResponder.Create(ProviderKind.Codex, operatorApproval));
        Assert.Equal(ProviderDisclosureSnapshot.DenialMessage, denied.Message);
        var evalApproval = ProviderDisclosureTestPolicy.Allow("codex", "fake-model", Remote.Destination, "devops-blind-eval");
        var missingKey = Assert.Throws<InvalidOperationException>(() => AgentBlindEvalResponder.Create(ProviderKind.Codex, evalApproval));
        Assert.Contains("HONUA_DEVOPS_CODEX_API_KEY", missingKey.Message);
    }

    [Fact]
    public void DenialAndMovedTransportEndpoint_SendNothing()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "fake-model");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "fake-key");
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", Remote.Destination);
        using TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient http = new(handler);
        var options = new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) };
        Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(ProviderKind.Codex, "synthetic", new(null),
            ProviderModelUseCase.Operator, clientOptionsOverride: options));
        options.Endpoint = new Uri("https://other.test/v1");
        Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(ProviderKind.Codex, "synthetic", Snapshot(Rule()),
            ProviderModelUseCase.Operator, clientOptionsOverride: options));
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task ApprovedDefaultRoute_UsesExplicitActualWireDestinationAndModel()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "fake-model");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "fake-key");
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", null);
        string fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures/nim-chat-completion.json"));
        using TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(fixture, System.Text.Encoding.UTF8, "application/json") });
        using HttpClient http = new(handler);
        var options = new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) };
        var configured = AgentProviderFactory.Create(ProviderKind.Codex, "synthetic",
            ProviderDisclosureTestPolicy.Allow("codex", "fake-model", "https://api.openai.com/v1"),
            ProviderModelUseCase.Operator, clientOptionsOverride: options);
        await configured.Agent.RunAsync("synthetic prompt");
        Assert.Equal(new Uri("https://api.openai.com/v1"), options.Endpoint);
        var sent = Assert.Single(handler.CapturedRequests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", sent.Uri);
        Assert.Contains("\"model\":\"fake-model\"", sent.Body!);
    }

    [Fact]
    public void ExactBedrockApproval_ReachesOnlyExplicitOfflineConstructionDependencies()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_BEDROCK_MODEL", "fake-profile");
        environment.Set("HONUA_DEVOPS_BEDROCK_REGION", "us-east-2");
        int credentialReads = 0, constructors = 0;
        var hooks = new ProviderConstructionHooks(route =>
        {
            credentialReads++;
            return new(route.Name, route.Model, "fake-key", route.Endpoint, route.Region);
        }, _ => { constructors++; throw new InvalidOperationException("synthetic constructor reached"); });
        var approval = ProviderDisclosureTestPolicy.Allow("bedrock", "fake-profile", "aws-bedrock:us-east-2", kind: "bedrock");
        var result = Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(ProviderKind.Bedrock,
            "synthetic", approval, ProviderModelUseCase.Operator, constructionHooks: hooks));
        Assert.Equal("synthetic constructor reached", result.Message);
        Assert.Equal(1, credentialReads);
        Assert.Equal(1, constructors);
    }

    private static Dictionary<string, string> Rule() => new()
    {
        ["provider"] = "codex", ["kind"] = "openai", ["destination"] = Remote.Destination,
        ["model"] = "fake-model", ["useCase"] = "devops-operator", ["dataClassification"] = "unclassified"
    };
    private static ProviderDisclosureSnapshot Snapshot(Dictionary<string, string> rule) =>
        new(JsonSerializer.Serialize(new { workloadDataClassification = "unclassified", approvals = new[] { rule } }));
}
