using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed record InstallHandoffVerificationRequest(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string AdminKeySecretReference,
    string BaseUrl,
    string CandidateReference,
    string ProxyPackage,
    string ProxyIntegrity,
    string ProvisioningOperationId,
    IReadOnlyList<string> RequiredTools,
    TimeSpan? VerificationDeadline = null);

internal sealed record InstallHandoffVerificationResult(
    bool Succeeded,
    string Status,
    string Detail,
    string? ServerIdentity,
    IReadOnlyList<string> ObservedTools,
    IReadOnlyList<OperationBackendStep> Steps,
    string? ServerEndpointIdentity = null,
    string? ObservedRosterSha256 = null,
    int? ChildExitCode = null,
    bool ChildReaped = false,
    bool SecretScanPassed = false);

internal interface IInstallHandoffVerifier
{
    Task<InstallHandoffVerificationResult> VerifyAsync(
        InstallHandoffVerificationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs the emitted proxy contract and verifies the installed cell without persisting its secret.</summary>
internal sealed class SystemInstallHandoffVerifier : IInstallHandoffVerifier
{
    internal static SystemInstallHandoffVerifier Instance { get; } = new();

    private readonly string npmCommand;

    internal SystemInstallHandoffVerifier(string npmCommand = "npm")
    {
        this.npmCommand = npmCommand;
    }

    public async Task<InstallHandoffVerificationResult> VerifyAsync(
        InstallHandoffVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.VerificationDeadline ?? TimeSpan.FromSeconds(45));
        CancellationToken verificationToken = deadline.Token;
        List<OperationBackendStep> steps = [];
        string? adminKey;
        try
        {
            adminKey = await ResolveSecretAsync(request.AdminKeySecretReference, verificationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failed("secret-resolution-failed", Redaction.Scrub(exception.Message), steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("handoff-verification-timeout", "The bounded handoff verification timed out during secret resolution.", steps);
        }
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            return Failed("secret-resolution-failed", "The admin-key reference could not be resolved.", steps);
        }

        ProcessCapture integrity;
        try
        {
            integrity = await RunCaptureAsync(
                npmCommand, ["view", request.ProxyPackage, "dist.integrity", "--json"], null, null,
                TimeSpan.FromMinutes(2), verificationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failed("proxy-registry-unavailable", Redaction.Scrub(exception.Message), steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("handoff-verification-timeout", "The bounded handoff verification timed out during package verification.", steps);
        }
        string observedIntegrity = integrity.StandardOutput.Trim().Trim('"');
        bool integrityOk = integrity.ExitCode == 0
            && string.Equals(observedIntegrity, request.ProxyIntegrity, StringComparison.Ordinal);
        steps.Add(new OperationBackendStep(
            "verify-proxy-integrity", request.ProxyPackage, integrityOk,
            integrityOk ? "registry integrity matches the manifest pin" : "registry integrity mismatch",
            integrityOk ? request.ProxyIntegrity : "<redacted>", false));
        if (!integrityOk)
        {
            return Failed("proxy-integrity-mismatch", "The pinned proxy package integrity could not be verified.", steps);
        }

        using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
        Uri baseUri = new(request.BaseUrl.TrimEnd('/') + "/");
        HttpResponseMessage health;
        try
        {
            health = await http.GetAsync(new Uri(baseUri, "healthz/ready"), verificationToken);
        }
        catch (HttpRequestException exception)
        {
            return Failed("handoff-health-failed", Redaction.Scrub(exception.Message), steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("handoff-verification-timeout", "The bounded handoff verification timed out during readiness verification.", steps);
        }
        using (health)
        {
        steps.Add(HttpStep("verify-health", new Uri(baseUri, "healthz/ready"), health));
        if (!health.IsSuccessStatusCode)
        {
            return Failed("handoff-health-failed", "The installed server readiness endpoint was not healthy.", steps);
        }
        }

        using HttpRequestMessage identityRequest = new(HttpMethod.Get, new Uri(baseUri, "api/v1/admin/version"));
        identityRequest.Headers.TryAddWithoutValidation("X-API-Key", adminKey);
        HttpResponseMessage identityResponse;
        try
        {
            identityResponse = await http.SendAsync(identityRequest, verificationToken);
        }
        catch (HttpRequestException exception)
        {
            return Failed("handoff-auth-failed", Redaction.Scrub(exception.Message), steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("handoff-verification-timeout", "The bounded handoff verification timed out during authenticated identity verification.", steps);
        }
        string identityBytes;
        using (identityResponse)
        {
        identityBytes = await identityResponse.Content.ReadAsStringAsync(verificationToken);
        steps.Add(HttpStep("verify-auth-and-server-identity", identityRequest.RequestUri!, identityResponse));
        if (!identityResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(identityBytes))
        {
            return Failed("handoff-auth-failed", "The authenticated Admin identity probe failed.", steps);
        }
        if (!ContainsExactStructuredValue(identityBytes, request.CandidateReference))
        {
            return Failed(
                "candidate-identity-mismatch",
                "The authenticated server identity does not contain an exact structured value matching the manifest-pinned candidate reference.",
                steps);
        }
        }

        ProcessStartInfo start = new(request.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach ((string name, string value) in request.Environment)
        {
            start.Environment[name] = value;
        }
        start.Environment["HONUA_ADMIN_KEY"] = adminKey;
        using Process proxy = new() { StartInfo = start };
        try
        {
            if (!proxy.Start())
            {
                return Failed("proxy-start-failed", "The emitted proxy command did not start.", steps);
            }
            JsonElement initialize = await RpcAsync(proxy, 1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "honua-devops-handoff-verifier", version = "1" }
            }, verificationToken);
            if (!initialize.TryGetProperty("result", out _))
            {
                return Failed("mcp-initialize-failed", "The proxy did not complete MCP initialize.", steps);
            }
            await proxy.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            await proxy.StandardInput.FlushAsync(verificationToken);

            HashSet<string> tools = new(StringComparer.Ordinal);
            HashSet<string> cursors = new(StringComparer.Ordinal);
            string? cursor = null;
            int requestId = 2;
            do
            {
                JsonElement response = await RpcAsync(proxy, requestId++, "tools/list",
                    cursor is null ? new { } : new { cursor }, verificationToken);
                if (!response.TryGetProperty("result", out JsonElement result)
                    || !result.TryGetProperty("tools", out JsonElement roster)
                    || roster.ValueKind != JsonValueKind.Array)
                {
                    return Failed("mcp-roster-malformed", "MCP tools/list returned a malformed roster.", steps);
                }
                foreach (JsonElement tool in roster.EnumerateArray())
                {
                    if (tool.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    {
                        tools.Add(name.GetString()!);
                    }
                }
                cursor = result.TryGetProperty("nextCursor", out JsonElement next)
                    && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (!string.IsNullOrWhiteSpace(cursor) && !cursors.Add(cursor))
                {
                    return Failed("mcp-pagination-loop", "MCP tools/list repeated a pagination cursor.", steps);
                }
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            string[] missing = request.RequiredTools.Where(tool => !tools.Contains(tool)).Order().ToArray();
            steps.Add(new OperationBackendStep(
                "verify-mcp-roster", "mcp://tools/list", missing.Length == 0,
                missing.Length == 0 ? $"all {request.RequiredTools.Count} required tools present" : $"missing: {string.Join(", ", missing)}",
                $"observed={tools.Count}", false));
            if (missing.Length > 0)
            {
                return Failed("mcp-roster-incomplete", $"Required MCP tools are missing: {string.Join(", ", missing)}.", steps);
            }

            JsonElement statusCall = await RpcAsync(proxy, requestId, "tools/call",
                new { name = "honua_admin_server_status", arguments = new { } }, verificationToken);
            bool callOk = statusCall.TryGetProperty("result", out JsonElement callResult)
                && (!callResult.TryGetProperty("isError", out JsonElement isError) || isError.ValueKind != JsonValueKind.True);
            steps.Add(new OperationBackendStep(
                "verify-admin-status-call", "mcp://tools/honua_admin_server_status", callOk,
                callOk ? "authenticated harmless Admin status call succeeded" : "Admin status call failed",
                "<response-redacted>", false));
            if (!callOk)
            {
                return Failed("admin-status-call-failed", "The authenticated Admin status call failed.", steps);
            }

            await ReapAsync(proxy);
            string[] observedTools = tools.Order().ToArray();
            return new InstallHandoffVerificationResult(
                true,
                "install-handoff-verified",
                "Health, authentication, MCP initialization, paged roster, and Admin status call succeeded.",
                ComputeSha256(Encoding.UTF8.GetBytes(identityBytes)),
                observedTools,
                steps,
                ComputeSha256(Encoding.UTF8.GetBytes(baseUri.AbsoluteUri.TrimEnd('/'))),
                ComputeSha256(Encoding.UTF8.GetBytes(string.Join("\n", observedTools))),
                proxy.ExitCode,
                ChildReaped: true,
                SecretScanPassed: true);
        }
        catch (VerificationFailure exception)
        {
            return Failed(exception.Status, exception.Message, steps);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or TimeoutException)
        {
            return Failed("handoff-verification-failed", Redaction.Scrub(exception.Message), steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("handoff-verification-timeout", "The bounded handoff verification timed out.", steps);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(adminKey));
            await ReapAsync(proxy);
        }
    }

    private static async Task ReapAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
            // Process.Start returned false or threw before an OS process existed.
        }
    }

    private static async Task<JsonElement> RpcAsync(
        Process process, int id, string method, object parameters, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id, method, @params = parameters
        }));
        await process.StandardInput.FlushAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new IOException("The proxy closed stdout before returning an MCP response.");
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                throw new VerificationFailure("mcp-stdout-noise", "The proxy wrote a non-JSON line to MCP stdout.");
            }
            using (document)
            {
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement responseId) || !responseId.TryGetInt32(out int value))
            {
                throw new VerificationFailure("mcp-response-malformed", "The proxy returned an MCP response without an integer id.");
            }
            if (value != id)
            {
                throw new VerificationFailure("mcp-response-out-of-order", "The proxy returned an MCP response for a different request id.");
            }
            return root.Clone();
            }
        }
    }

    private static bool ContainsExactStructuredValue(string responseJson, string expected)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseJson);
            return ContainsExactValue(document.RootElement, expected);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsExactValue(JsonElement element, string expected)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsExactValue(property.Value, expected)),
            JsonValueKind.Array => element.EnumerateArray().Any(value => ContainsExactValue(value, expected)),
            _ => false
        };
    }

    private static async Task<string?> ResolveSecretAsync(string reference, CancellationToken cancellationToken)
    {
        if (reference.StartsWith("secret://", StringComparison.Ordinal))
        {
            string variable = reference["secret://".Length..];
            return variable.Length > 0 && variable.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? Environment.GetEnvironmentVariable(variable)
                : null;
        }
        if (reference.StartsWith("arn:aws:secretsmanager:", StringComparison.Ordinal)
            || reference.StartsWith("aws-secretsmanager://", StringComparison.Ordinal))
        {
            string secretId = reference.StartsWith("aws-secretsmanager://", StringComparison.Ordinal)
                ? reference["aws-secretsmanager://".Length..]
                : reference;
            ProcessCapture result = await RunCaptureAsync(
                "aws", ["secretsmanager", "get-secret-value", "--secret-id", secretId, "--query", "SecretString", "--output", "text"],
                null, null, TimeSpan.FromMinutes(1), cancellationToken);
            return result.ExitCode == 0 ? result.StandardOutput.TrimEnd('\r', '\n') : null;
        }
        if (reference.StartsWith("azure-key-vault://", StringComparison.Ordinal)
            || reference.StartsWith("https://", StringComparison.Ordinal)
                && reference.Contains(".vault.azure.net/secrets/", StringComparison.OrdinalIgnoreCase))
        {
            string secretId = reference.StartsWith("azure-key-vault://", StringComparison.Ordinal)
                ? "https://" + reference["azure-key-vault://".Length..]
                : reference;
            ProcessCapture result = await RunCaptureAsync(
                "az", ["keyvault", "secret", "show", "--id", secretId, "--query", "value", "--output", "tsv"],
                null, null, TimeSpan.FromMinutes(1), cancellationToken);
            return result.ExitCode == 0 ? result.StandardOutput.TrimEnd('\r', '\n') : null;
        }
        return null;
    }

    private static async Task<ProcessCapture> RunCaptureAsync(
        string command, IReadOnlyList<string> arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach ((string name, string value) in environment) start.Environment[name] = value;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start `{command}`.");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await process.WaitForExitAsync(linked.Token);
            return new ProcessCapture(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static OperationBackendStep HttpStep(string name, Uri uri, HttpResponseMessage response)
        => new(name, uri.ToString(), response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}", "<response-redacted>", false);

    private static InstallHandoffVerificationResult Failed(
        string status, string detail, IReadOnlyList<OperationBackendStep> steps)
        => new(false, status, detail, null, [], steps);

    private sealed record ProcessCapture(int ExitCode, string StandardOutput, string StandardError);

    private sealed class VerificationFailure(string status, string message) : Exception(message)
    {
        internal string Status { get; } = status;
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
