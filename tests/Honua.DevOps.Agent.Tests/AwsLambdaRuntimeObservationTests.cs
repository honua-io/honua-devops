using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class AwsLambdaRuntimeObservationTests
{
    private const string Function = "arn:aws:lambda:us-west-2:123456789012:function:honua";
    private const string Alias = Function + ":live";
    private static readonly string Digest = "sha256:" + new string('a', 64);
    private const string Endpoint = "https://api123.execute-api.us-west-2.amazonaws.com";

    [Theory]
    [InlineData("wrong-account")]
    [InlineData("wrong-version")]
    [InlineData("weighted-alias")]
    [InlineData("wrong-image")]
    [InlineData("wrong-route")]
    [InlineData("wrong-integration")]
    [InlineData("wrong-stage")]
    [InlineData("disabled-endpoint")]
    public async Task InconsistentProviderStateRefusesWithoutPersistingRawOutput(string scenario)
    {
        var runner = new Reader(scenario);
        var observer = new AwsLambdaRuntimeObservation(runner);
        await Assert.ThrowsAsync<InvalidDataException>(() => observer.ObserveAsync(Facts(), Endpoint, Digest, CancellationToken.None));
        Assert.All(runner.Commands, command =>
        {
            Assert.DoesNotContain(command, argument => argument is "update-alias" or "update-function-code" or "create-integration");
            Assert.Contains("--query", command);
        });
    }

    [Fact]
    public async Task ServingPublishedVersionAndActualRouteIntegrationProduceCredentialFreeObservation()
    {
        var runner = new Reader("valid");
        var result = await new AwsLambdaRuntimeObservation(runner).ObserveAsync(Facts(), Endpoint, Digest, CancellationToken.None);
        Assert.Equal(Alias, result.ResourceIdentity.GetProperty("aliasArn").GetString());
        Assert.Equal(64, result.EvidenceSha256.Length);
        Assert.DoesNotContain("DO_NOT_PERSIST_PROVIDER_SECRET", result.EvidenceJson);
        Assert.Contains("registry.example/honua@" + Digest, result.EvidenceJson);
        Assert.Contains(runner.Commands, command => command.Contains("get-function") && command.Contains("--qualifier") && command.Contains("42"));
        Assert.DoesNotContain(runner.Commands.SelectMany(command => command), value => value.Contains("Configuration.Environment", StringComparison.Ordinal));
        Assert.All(runner.Executables, executable => Assert.Equal("aws", executable));
    }

    [Fact]
    public async Task CustomDomainCannotQualifyFromUnusedApiMappingWithoutPublicDnsProof()
    {
        var runner = new Reader("valid");
        await Assert.ThrowsAsync<InvalidDataException>(() => new AwsLambdaRuntimeObservation(runner)
            .ObserveAsync(Facts(), "https://other.example.test", Digest, CancellationToken.None));
        Assert.DoesNotContain(runner.Commands, command => command.Contains("get-domain-name"));
    }

    private static ProvisionedRuntimeFacts Facts() => new("aws-lambda", "AwsLambda", "honua-gitops-aws-lambda",
        "honua-live", "dev", "123456789012", "us-west-2", "aws", JsonSerializer.SerializeToElement(new
        {
            functionArn = Function, aliasArn = Alias, publishedVersion = "42", apiGatewayId = "api123",
            apiGatewayIntegrationId = "int123", apiGatewayIntegrationAliasArn = Alias,
            apiGatewayStage = "$default", apiGatewayRouteId = "route123", apiGatewayRouteKey = "ANY /{proxy+}"
        }));

    internal sealed class Reader(string scenario, ProvisionedRuntimeFacts? facts = null, string endpoint = Endpoint) : IProvisioningProcessRunner
    {
        private string Function => facts?.ResourceIdentity.GetProperty("functionArn").GetString() ?? AwsLambdaRuntimeObservationTests.Function;
        private string Alias => facts?.ResourceIdentity.GetProperty("aliasArn").GetString() ?? AwsLambdaRuntimeObservationTests.Alias;
        private string Version => facts?.ResourceIdentity.GetProperty("publishedVersion").GetString() ?? "42";
        private string Region => facts?.Region ?? "us-west-2";
        private string Api => facts?.ResourceIdentity.GetProperty("apiGatewayId").GetString() ?? "api123";
        private string Integration => facts?.ResourceIdentity.GetProperty("apiGatewayIntegrationId").GetString() ?? "int123";
        private string Route => facts?.ResourceIdentity.GetProperty("apiGatewayRouteId").GetString() ?? "route123";
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<string> Executables { get; } = [];
        public string? ScenarioOverride { get; set; }
        public bool CanRun(string fileName) => fileName == "aws";
        public Task<ProvisioningProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(environment);
            Assert.Equal("true", environment["AWS_IGNORE_CONFIGURED_ENDPOINT_URLS"]);
            Executables.Add(fileName); Commands.Add(arguments);
            var activeScenario = ScenarioOverride ?? scenario;
            object response = arguments[1] switch
            {
                "get-caller-identity" => new { account = activeScenario == "wrong-account" ? "999999999999" : "123456789012", arn = "arn:aws:sts::123456789012:assumed-role/host/session" },
                "get-alias" => new { arn = Alias, version = activeScenario == "wrong-version" ? Version + "0" : Version,
                    weights = activeScenario == "weighted-alias" ? new Dictionary<string, double> { ["43"] = 0.1 } : [] },
                "get-function" => new { arn = Function + ":" + Version, version = Version, state = "Active", lastUpdateStatus = "Successful",
                    image = "registry.example/honua@" + (activeScenario == "wrong-image" ? "sha256:" + new string('b', 64) : Digest),
                    secret = "DO_NOT_PERSIST_PROVIDER_SECRET" },
                "get-route" => new { id = Route, key = "ANY /{proxy+}", target = activeScenario == "wrong-route" ? "integrations/other" : "integrations/" + Integration },
                "get-integration" => new { id = Integration, type = "AWS_PROXY", uri = activeScenario == "wrong-integration" ? Function + ":other" :
                    "arn:aws:apigateway:" + Region + ":lambda:path/2015-03-31/functions/" + Alias + "/invocations" },
                "get-api" => new { id = Api, endpoint, disabled = activeScenario == "disabled-endpoint" },
                "get-stage" => new { name = "$default", autoDeploy = activeScenario != "wrong-stage" },
                _ => new { incomplete = true }
            };
            return Task.FromResult(new ProvisioningProcessResult(0, JsonSerializer.Serialize(response), "", false));
        }
    }
}
