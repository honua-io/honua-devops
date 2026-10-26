using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.DevOps.Agent.Configuration;

namespace Honua.DevOps.Agent.Providers;

internal enum ProviderModelUseCase { Operator, BlindEval }

/// <summary>Immutable approval provenance captured before working-directory configuration.</summary>
internal sealed class ProviderDisclosureSnapshot(string? processPolicy)
{
    internal const string EnvironmentVariable = "HONUA_DEVOPS_AI_DISCLOSURE_POLICY";
    internal const string DenialMessage = "ai-disclosure-denied: model transfer requires exact operator approval.";
    private readonly string? _processPolicy = processPolicy;

    internal static ProviderDisclosureSnapshot CaptureAndLoadDotEnv(string directory)
    {
        var snapshot = new ProviderDisclosureSnapshot(Environment.GetEnvironmentVariable(EnvironmentVariable));
        _ = DotEnvLoader.LoadFromDirectory(directory);
        return snapshot;
    }

    internal void RequireApproval(ProviderRoute route, ProviderModelUseCase useCase)
    {
        if (!IsAllowed(route, useCase)) throw new InvalidOperationException(DenialMessage);
    }

    internal bool IsAllowed(ProviderRoute route, ProviderModelUseCase useCase)
    {
        DisclosureOptions? options;
        try
        {
            if (_processPolicy is { Length: > 65536 } || (_processPolicy is not null && Encoding.UTF8.GetByteCount(_processPolicy) > 65536))
                return false;
            options = _processPolicy is null ? new DisclosureOptions() :
                JsonSerializer.Deserialize(_processPolicy, ProviderDisclosureJsonContext.Default.DisclosureOptions);
        }
        catch (JsonException) { return false; }
        if (options is null || !Bounded(options.WorkloadDataClassification, 64)
            || options.Approvals is null || options.Approvals.Length > 128 || !Enum.IsDefined(useCase)
            || !Enum.IsDefined(route.Provider)
            || options.Approvals.Any(rule => rule is null || !rule.IsValid)) return false;
        string? destination = NormalizeDestination(route.Kind, route.Destination, out bool loopback);
        if (destination is null || !Bounded(route.Model, 2048)) return false;
        if (loopback) return true; // Requires the production nonredirecting transport.
        string purpose = UseCaseName(useCase);
        return options.Approvals.Any(rule =>
            string.Equals(rule.Provider, route.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rule.Kind, route.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeDestination(rule.Kind, rule.Destination, out _), destination, StringComparison.Ordinal)
            && string.Equals(rule.Model, route.Model, StringComparison.Ordinal)
            && string.Equals(rule.UseCase, purpose, StringComparison.Ordinal)
            && string.Equals(rule.DataClassification, options.WorkloadDataClassification, StringComparison.Ordinal));
    }

    internal static string UseCaseName(ProviderModelUseCase useCase) => useCase switch
    {
        ProviderModelUseCase.Operator => "devops-operator",
        ProviderModelUseCase.BlindEval => "devops-blind-eval",
        _ => throw new ArgumentOutOfRangeException(nameof(useCase))
    };

    private static string? NormalizeDestination(string kind, string destination, out bool loopback)
    {
        loopback = false;
        if (!Bounded(destination, 2048)) return null;
        if (string.Equals(kind, "bedrock", StringComparison.OrdinalIgnoreCase))
        {
            const string prefix = "aws-bedrock:";
            if (!destination.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string region = destination[prefix.Length..];
            return region.Length > 0 && region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') ? destination : null;
        }
        if (!string.Equals(kind, "openai", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        string host = uri.Host.Trim('[', ']');
        loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address));
        return loopback || uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
    }

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    internal sealed class DisclosureOptions
    {
        public string WorkloadDataClassification { get; set; } = "unclassified";
        public DisclosureRule[] Approvals { get; set; } = [];
    }

    internal sealed class DisclosureRule
    {
        public string Provider { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Model { get; set; } = "";
        public string UseCase { get; set; } = "";
        public string DataClassification { get; set; } = "";
        [JsonIgnore]
        public bool IsValid => Bounded(Provider, 32) && Bounded(Kind, 32) && Bounded(Destination, 2048)
            && Bounded(Model, 2048) && Bounded(UseCase, 64) && Bounded(DataClassification, 64);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProviderDisclosureSnapshot.DisclosureOptions))]
internal sealed partial class ProviderDisclosureJsonContext : JsonSerializerContext;

/// <summary>Nonsecret route loaded once, authorized before provider credentials are read.</summary>
internal sealed record ProviderRoute(ProviderKind Provider, string Model, Uri? Endpoint, string? Region)
{
    internal string Name => Provider.ToConfigValue();
    internal string Kind => Provider == ProviderKind.Bedrock ? "bedrock" : "openai";
    internal string Destination => Provider == ProviderKind.Bedrock ? $"aws-bedrock:{Region}" : Endpoint?.AbsoluteUri ?? string.Empty;
}
