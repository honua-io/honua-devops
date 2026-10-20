using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed record RuntimeProviderObservation(JsonElement ResourceIdentity, DateTimeOffset ObservedAtUtc,
    string EvidenceJson, string EvidenceSha256);

/// <summary>Read-only provider observation of an exact applied Lambda/API Gateway projection.</summary>
internal sealed class AwsLambdaRuntimeObservation(IProvisioningProcessRunner runner)
{
    internal async Task<RuntimeProviderObservation> ObserveAsync(ProvisionedRuntimeFacts facts, string endpoint,
        string expectedImageDigest, CancellationToken cancellationToken)
    {
        if (facts.RuntimeKind != "aws-lambda" || facts.Backend != "honua-gitops-aws-lambda" ||
            !IsDigest(expectedImageDigest) || !runner.CanRun("aws")) Refuse();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != "https" ||
            publicUrl.UserInfo.Length != 0 || publicUrl.Query.Length != 0 || publicUrl.Fragment.Length != 0) Refuse();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        var identity = facts.ResourceIdentity;
        var functionArn = Text(identity, "functionArn");
        var aliasArn = Text(identity, "aliasArn");
        var version = Text(identity, "publishedVersion");
        var apiId = Text(identity, "apiGatewayId");
        var integrationId = Text(identity, "apiGatewayIntegrationId");
        var stage = Text(identity, "apiGatewayStage");
        var routeId = Text(identity, "apiGatewayRouteId");
        if (!aliasArn.StartsWith(functionArn + ":", StringComparison.Ordinal) || stage != "$default" &&
            (stage.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(stage[0]) || stage.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))) Refuse();
        if (new[] { apiId, integrationId, routeId }.Any(value => value.Length is < 1 or > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character)))) Refuse();
        var alias = aliasArn[(functionArn.Length + 1)..];
        if (alias.All(char.IsAsciiDigit) ||
            alias.Length == 0 || alias.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
            version.Length == 0 || !version.All(char.IsAsciiDigit) || version.StartsWith('0')) Refuse();
        using var caller = await ReadAsync(facts, ["sts", "get-caller-identity", "--query", "{account:Account,arn:Arn}"], bounded.Token);
        if (Text(caller.RootElement, "account") != facts.AccountId ||
            !Text(caller.RootElement, "arn").StartsWith("arn:" + facts.Partition + ":", StringComparison.Ordinal)) Refuse();
        using var observedAlias = await ReadAsync(facts, ["lambda", "get-alias", "--function-name", functionArn, "--name", alias,
            "--query", "{arn:AliasArn,version:FunctionVersion,weights:RoutingConfig.AdditionalVersionWeights}"], bounded.Token);
        var selectedAlias = observedAlias.RootElement;
        if (Text(selectedAlias, "arn") != aliasArn || Text(selectedAlias, "version") != version ||
            selectedAlias.TryGetProperty("weights", out var weights) && weights.ValueKind != JsonValueKind.Null &&
            (weights.ValueKind != JsonValueKind.Object || weights.EnumerateObject().Any())) Refuse();
        // The query deliberately excludes Configuration.Environment, credentials and all
        // secret values. It observes the immutable published version, never $LATEST.
        using var function = await ReadAsync(facts, ["lambda", "get-function", "--function-name", functionArn, "--qualifier", version,
            "--query", "{arn:Configuration.FunctionArn,version:Configuration.Version,state:Configuration.State,lastUpdateStatus:Configuration.LastUpdateStatus,image:Code.ResolvedImageUri}"], bounded.Token);
        var selectedFunction = function.RootElement;
        var image = Text(selectedFunction, "image");
        var observedArn = Text(selectedFunction, "arn");
        if (observedArn != functionArn && observedArn != functionArn + ":" + version ||
            Text(selectedFunction, "version") != version || Text(selectedFunction, "state") != "Active" ||
            Text(selectedFunction, "lastUpdateStatus") != "Successful" || !image.EndsWith("@" + expectedImageDigest, StringComparison.Ordinal)) Refuse();
        using var route = await ReadAsync(facts, ["apigatewayv2", "get-route", "--api-id", apiId, "--route-id", routeId,
            "--query", "{id:RouteId,key:RouteKey,target:Target}"], bounded.Token);
        if (Text(route.RootElement, "id") != routeId || Text(route.RootElement, "key") != Text(identity, "apiGatewayRouteKey") ||
            Text(route.RootElement, "target") != "integrations/" + integrationId) Refuse();
        using var integration = await ReadAsync(facts, ["apigatewayv2", "get-integration", "--api-id", apiId, "--integration-id", integrationId,
            "--query", "{id:IntegrationId,type:IntegrationType,uri:IntegrationUri}"], bounded.Token);
        var uri = Text(integration.RootElement, "uri");
        var invocationArn = $"arn:{facts.Partition}:apigateway:{facts.Region}:lambda:path/2015-03-31/functions/{aliasArn}/invocations";
        if (Text(integration.RootElement, "id") != integrationId || Text(integration.RootElement, "type") != "AWS_PROXY" ||
            uri != aliasArn && uri != invocationArn || Text(identity, "apiGatewayIntegrationAliasArn") != aliasArn) Refuse();
        using var api = await ReadAsync(facts, ["apigatewayv2", "get-api", "--api-id", apiId,
            "--query", "{id:ApiId,endpoint:ApiEndpoint,disabled:DisableExecuteApiEndpoint}"], bounded.Token);
        if (Text(api.RootElement, "id") != apiId) Refuse();
        using var observedStage = await ReadAsync(facts, ["apigatewayv2", "get-stage", "--api-id", apiId, "--stage-name", stage,
            "--query", "{name:StageName,autoDeploy:AutoDeploy}"], bounded.Token);
        if (Text(observedStage.RootElement, "name") != stage ||
            !observedStage.RootElement.TryGetProperty("autoDeploy", out var automatic) || automatic.ValueKind != JsonValueKind.True) Refuse();
        if (!Uri.TryCreate(Text(api.RootElement, "endpoint"), UriKind.Absolute, out var apiUrl)) Refuse();
        if (publicUrl!.IdnHost == apiUrl!.IdnHost)
        {
            var expectedPath = stage == "$default" ? "" : "/" + stage;
            if (publicUrl.Port != 443 || publicUrl.AbsolutePath.TrimEnd('/') != expectedPath ||
                api.RootElement.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True) Refuse();
        }
        else
        {
            // A configured API Gateway mapping alone cannot prove public DNS serves it.
            // Initial v2 qualification supports only the observed default execute-api host.
            Refuse();
        }
        var observedAt = DateTimeOffset.UtcNow;
        var evidence = JsonSerializer.Serialize(new
        {
            schemaVersion = "honua.devops.runtime-observation/v1", runtimeKind = facts.RuntimeKind,
            facts.TargetKind, facts.Backend, facts.TargetId, facts.Environment,
            platform = new { facts.AccountId, facts.Region, facts.Partition },
            resourceIdentity = identity, endpoint, publishedImage = image,
            observedAtUtc = observedAt,
            scope = "Read-only provider configuration and immutable published image. Public Honua readiness, identity and MCP probes remain separately required."
        });
        return new(identity, observedAt, evidence, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))));
    }

    private async Task<JsonDocument> ReadAsync(ProvisionedRuntimeFacts facts, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("aws", [.. arguments, "--region", facts.Region, "--output", "json", "--no-cli-pager"],
            System.Environment.CurrentDirectory, TimeSpan.FromSeconds(20),
            new Dictionary<string, string> { ["AWS_PAGER"] = "", ["AWS_CLI_AUTO_PROMPT"] = "off", ["AWS_IGNORE_CONFIGURED_ENDPOINT_URLS"] = "true" }, cancellationToken);
        if (!result.Succeeded || result.StandardOutput.Length > 1024 * 1024) Refuse();
        try { return JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { Refuse(); throw; }
    }

    private static string Text(JsonElement value, string property)
        => value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 } text
            ? text : throw new InvalidDataException("The provider observation is incomplete.");
    private static bool IsDigest(string digest) => digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length == 71 && digest[7..].All(char.IsAsciiHexDigit);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse() => throw new InvalidDataException("The live provider observation does not match the exact approved runtime projection.");
}
