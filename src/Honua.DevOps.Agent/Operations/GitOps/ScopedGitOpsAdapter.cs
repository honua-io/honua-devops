using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.GitOps;

// This adapter writes review branches/PRs only. The server's existing deploy proposal and
// approval inbox remain the only deployment executor: no main push, merge or cloud rollback.
internal sealed record ScopedGitConfiguration(
    string Remote, string Repository, string BaseBranch, string[] AllowedPrefixes,
    string? GitHubToken, string GitExecutable = "git", string AuthorName = "", string AuthorEmail = "")
{
    internal static ScopedGitConfiguration? Load()
    {
        string? remote = Environment.GetEnvironmentVariable("HONUA_DEVOPS_SCOPED_GIT_REMOTE");
        if (string.IsNullOrWhiteSpace(remote)) return null;
        string repository = Environment.GetEnvironmentVariable("HONUA_DEVOPS_SCOPED_GIT_REPOSITORY") ?? "";
        string branch = Environment.GetEnvironmentVariable("HONUA_DEVOPS_SCOPED_GIT_BASE_BRANCH") ?? "trunk";
        string[] prefixes = (Environment.GetEnvironmentVariable("HONUA_DEVOPS_SCOPED_GIT_ALLOWED_PREFIXES") ?? "desired-state/releases/")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(remote, repository, branch, prefixes, Environment.GetEnvironmentVariable("HONUA_DEVOPS_GITHUB_TOKEN"),
            AuthorName: Environment.GetEnvironmentVariable("HONUA_DEVOPS_SCOPED_GIT_AUTHOR_NAME") ?? "",
            AuthorEmail: Environment.GetEnvironmentVariable("HONUA_DEVOPS_SCOPED_GIT_AUTHOR_EMAIL") ?? "");
    }
}

internal sealed record ScopedGitReviewEvidence(
    [property: JsonPropertyName("commitId")] string CommitId,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("pullRequestUrl")] string? PullRequestUrl,
    [property: JsonPropertyName("scopeDigest")] string ScopeDigest,
    [property: JsonPropertyName("operationId")] string? OperationId,
    [property: JsonPropertyName("remoteMutationAcknowledged")] bool RemoteMutationAcknowledged,
    [property: JsonPropertyName("receipt")] ProvisioningEvidenceReference? Receipt)
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion => "honua.devops.git-review/v1";
}

internal interface IScopedGitPullRequestClient
{
    Task<string> EnsureAsync(string branch, string expectedCommit, string title, string body, CancellationToken cancellationToken);
}

internal sealed class GitHubScopedPullRequestClient(ScopedGitConfiguration configuration) : IScopedGitPullRequestClient
{
    public async Task<string> EnsureAsync(string branch, string expectedCommit, string title, string body, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(configuration.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
            || string.IsNullOrWhiteSpace(configuration.GitHubToken))
            throw new InvalidOperationException("GitHub repository and credential must be host-configured.");
        using HttpClient client = HttpClientFactory.Create(TimeSpan.FromSeconds(30));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.GitHubToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("honua-devops");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string api = $"https://api.github.com/repos/{configuration.Repository}/pulls";
        string owner = configuration.Repository.Split('/')[0];
        using HttpResponseMessage found = await client.GetAsync(
            $"{api}?state=open&base={Uri.EscapeDataString(configuration.BaseBranch)}&head={Uri.EscapeDataString(owner + ":" + branch)}", cancellationToken);
        found.EnsureSuccessStatusCode();
        using JsonDocument existing = JsonDocument.Parse(await found.Content.ReadAsStringAsync(cancellationToken));
        if (existing.RootElement.GetArrayLength() > 0)
            return VerifiedUrl(existing.RootElement[0], branch, expectedCommit);
        using HttpResponseMessage created = await client.PostAsJsonAsync(api,
            new { head = branch, @base = configuration.BaseBranch, title, body, draft = false }, cancellationToken);
        created.EnsureSuccessStatusCode();
        using JsonDocument payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        return VerifiedUrl(payload.RootElement, branch, expectedCommit);
    }

    private string VerifiedUrl(JsonElement pullRequest, string branch, string commit)
    {
        if (pullRequest.GetProperty("head").GetProperty("sha").GetString() != commit
            || pullRequest.GetProperty("head").GetProperty("ref").GetString() != branch
            || pullRequest.GetProperty("base").GetProperty("ref").GetString() != configuration.BaseBranch)
            throw new InvalidOperationException("PR head/base no longer matches the canonical proposal.");
        return pullRequest.GetProperty("html_url").GetString()!;
    }
}

internal sealed class ScopedGitOpsAdapter(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel policy,
    ScopedGitConfiguration configuration,
    IAuditSink? auditSink = null,
    IProvisioningProcessRunner? processRunner = null,
    IScopedGitPullRequestClient? pullRequests = null)
{
    private readonly ActuationSpine _spine = new(runtime, policy, auditSink);
    private readonly ConsoleOperationBridge _bridge = new(runtime, gateway, policy, auditSink);
    private readonly IProvisioningProcessRunner _runner = processRunner ?? SystemProvisioningProcessRunner.Instance;
    private readonly IScopedGitPullRequestClient _pullRequests = pullRequests ?? new GitHubScopedPullRequestClient(configuration);
    private static readonly Regex CommitPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    internal async Task<OperationResponse> CreateAsync(string releasePackageJson, string expectedBaseCommit, CancellationToken cancellationToken = default)
    {
        if (!MetadataReleaseChangeSetBuilder.TryBuild(releasePackageJson, out MetadataReleaseChangeSet changeSet, out _)
            || changeSet.Readiness is MetadataChangeSetReadiness.Blocked or MetadataChangeSetReadiness.Unknown)
            return Refusal("git-changeset-blocked", "A compatible server metadata release package is required.");
        if (changeSet.Files.Count > 64 || changeSet.Files.Sum(file => (long)file.Content.Length) > 1024 * 1024
            || changeSet.Files.Any(file => !Allowed(file.Path)))
            return Refusal("git-scope-rejected", "Generated files exceed the host-configured configuration scope.");
        return await WriteAsync(changeSet.Service, changeSet.TargetEnvironments, expectedBaseCommit,
            changeSet.CommitMessage, changeSet.PrTitle, changeSet.PrBody, changeSet.Files, null, cancellationToken);
    }

    internal Task<OperationResponse> RevertAsync(string commitId, string expectedBaseCommit, string service,
        string environmentsCsv, CancellationToken cancellationToken = default)
    {
        if (!CommitPattern.IsMatch(commitId)) return Task.FromResult(Refusal("git-revert-rejected", "An immutable full commit id is required."));
        string normalizedService = DeploymentInputs.ValidateServiceName(service);
        string[] environments = DeploymentInputs.ParseEnvironments(environmentsCsv, runtime.AllowedEnvironments);
        return WriteAsync(normalizedService, environments, expectedBaseCommit,
            $"Revert scoped configuration {commitId}", $"Revert scoped configuration {commitId[..12]}",
            $"Review configuration recovery of commit `{commitId}`. No deployment or cloud rollback has executed.", [], commitId, cancellationToken);
    }

    private async Task<OperationResponse> WriteAsync(string service, IReadOnlyList<string> environments,
        string expectedBaseCommit, string message, string title, string body, IReadOnlyList<MetadataReleaseFile> files,
        string? revertCommit, CancellationToken cancellationToken)
    {
        if (!CommitPattern.IsMatch(expectedBaseCommit) || environments.Count == 0
            || environments.Any(environment => !runtime.AllowedEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase)))
            return Refusal("git-scope-rejected", "An immutable base and permitted target environments are required.");
        if (string.IsNullOrWhiteSpace(runtime.DeployTargetId))
            return Refusal("target-unconfigured", "A canonical server deployment target is required.");
        if (runtime.ExecutionMode != ExecutionMode.Execute || (int)runtime.ExecutionTier < (int)ExecutionTier.Propose)
            return Refusal("git-plan-only", "Scoped Git writes require execute mode and at least propose tier; no branch or PR was written.");
        if (pullRequests is null && (string.IsNullOrWhiteSpace(configuration.GitHubToken)
            || !Regex.IsMatch(configuration.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")))
            return Refusal("git-configuration-rejected", "GitHub repository and credential must be host-configured before any branch write.");
        if (string.IsNullOrWhiteSpace(configuration.AuthorName) || string.IsNullOrWhiteSpace(configuration.AuthorEmail)
            || configuration.AuthorName.Contains('\n') || configuration.AuthorEmail.Contains('\n'))
            return Refusal("git-configuration-rejected", "Git author identity must be explicitly host-configured.");
        if (configuration.AllowedPrefixes.Length == 0 || configuration.AllowedPrefixes.Any(prefix => !SafePath(prefix.TrimEnd('/')))
            || !Regex.IsMatch(configuration.BaseBranch, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
            || configuration.BaseBranch.Contains("..", StringComparison.Ordinal))
            return Refusal("git-configuration-rejected", "Invalid host Git scope or branch policy.");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { service, environments, expectedBaseCommit, files, revertCommit })))).ToLowerInvariant();
        string branch = $"honua/config-{digest[..24]}";
        if (!_spine.TryAuthorizeLocalMutation("git-review-branch", runtime.DeployTargetId, $"scoped-git:{digest}", digest, out _))
            return Refusal("audit-sink-unavailable", "Durable audit preflight refused the Git mutation.");
        string scratch = Directory.CreateTempSubdirectory("honua-scoped-git-").FullName;
        OperationResponse? proposal = null;
        ScopedGitReviewEvidence? gitEvidence = null;
        try
        {
            string observedBase = await Git(scratch, ["ls-remote", "--", configuration.Remote, $"refs/heads/{configuration.BaseBranch}"], cancellationToken);
            if (!observedBase.StartsWith(expectedBaseCommit + "\t", StringComparison.Ordinal))
                return Refusal("git-base-conflict", "Configured base branch changed; review a new diff before retrying.");
            await Git(scratch, ["clone", "--no-hardlinks", "--no-checkout", "--", configuration.Remote, "work"], cancellationToken);
            string work = Path.Combine(scratch, "work");
            await Git(work, ["checkout", "--detach", expectedBaseCommit], cancellationToken);
            if (revertCommit is not null)
            {
                // Only single-parent commits whose entire delta is configuration may be reverted.
                string parents = await Git(work, ["rev-list", "--parents", "-n", "1", revertCommit], cancellationToken);
                if (parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 2)
                    return Refusal("git-revert-rejected", "Merge/root commits cannot be reverted through this adapter.");
                await Git(work, ["merge-base", "--is-ancestor", revertCommit, expectedBaseCommit], cancellationToken);
                string changed = await Git(work, ["diff-tree", "--no-commit-id", "--name-only", "-r", revertCommit], cancellationToken);
                if (SplitPaths(changed).Length == 0 || SplitPaths(changed).Any(file => !Allowed(file)))
                    return Refusal("git-scope-rejected", "Revert includes files outside the configured configuration scope.");
                await Git(work, ["revert", "--no-commit", revertCommit], cancellationToken);
            }
            else
            {
                foreach (MetadataReleaseFile file in files)
                {
                    string destination = Path.Combine(work, file.Path);
                    // Existing symlinks can redirect writes even when the relative name is safe.
                    for (string? parent = destination; parent is not null && parent != work; parent = Path.GetDirectoryName(parent))
                        if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidOperationException("Symlink in configured scope.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await File.WriteAllTextAsync(destination, file.Content, cancellationToken);
                }
                await Git(work, ["add", "--", .. files.Select(file => file.Path)], cancellationToken);
            }
            string staged = await Git(work, ["diff", "--cached", "--name-only"], cancellationToken);
            string stagedModes = await Git(work, ["ls-files", "--stage"], cancellationToken);
            foreach (string file in SplitPaths(staged))
            {
                if (!Allowed(file)) return Refusal("git-scope-rejected", "Staged paths exceed configuration scope.");
                string? entry = stagedModes.Split('\n').FirstOrDefault(line => line.EndsWith("\t" + file, StringComparison.Ordinal));
                if (entry is not null && !entry.StartsWith("100644 ", StringComparison.Ordinal))
                    return Refusal("git-scope-rejected", "Only regular non-executable configuration files are permitted.");
            }
            string diff = await Git(work, ["diff", "--cached", "--no-ext-diff"], cancellationToken);
            if (diff.Length > 1024 * 1024 || Redaction.Scrub(diff) != diff)
                return Refusal("git-secret-or-size-rejected", "Configuration diff contains secret-like material or exceeds the review size limit.");
            if (SplitPaths(staged).Length == 0 || SplitPaths(staged).Any(file => !Allowed(file)))
                return Refusal("git-scope-rejected", "The staged change is empty or exceeds configuration scope.");
            await Git(work, ["commit", "--no-verify", "-m", Redaction.Scrub(message)], cancellationToken);
            string commit = (await Git(work, ["rev-parse", "HEAD"], cancellationToken)).Trim();
            // Open the SAME canonical deployment proposal used by Console before remote writes.
            // This never approves/submits it; only the separate server human inbox can do that.
            proposal = await _bridge.CreateGitOpsProposalAsync(service, string.Join(',', environments), commit,
                "sync", $"Scoped Git {(revertCommit is null ? "configuration" : "revert")} {digest}", "honua-devops", cancellationToken);
            if (proposal.ServerOperations.Count == 0)
                return new OperationResponse("git-proposal-unavailable", "Canonical proposal was not confirmed; reconcile the same request before retry. No remote branch or PR was written.",
                    [$"Review branch: {branch}", $"Scope digest: {digest}"], proposal.Actions, proposal.ValidationChecks, proposal.Risks,
                    BackendSteps: proposal.BackendSteps) { ServerOperations = proposal.ServerOperations };
            string operationId = proposal.ServerOperations[0].OperationId!;
            string latestBase = await Git(work, ["ls-remote", "--", configuration.Remote, $"refs/heads/{configuration.BaseBranch}"], cancellationToken);
            if (!latestBase.StartsWith(expectedBaseCommit + "\t", StringComparison.Ordinal))
                return Refusal("git-base-conflict", "Base changed while preparing the proposal; no remote branch was written.");
            // Create-only/non-force push. A replay can only push the identical deterministic commit.
            await Git(work, ["push", "--", "origin", $"{commit}:refs/heads/{branch}"], cancellationToken);
            gitEvidence = Persist(new(commit, branch, null, digest, operationId, true, null));
            string url = await _pullRequests.EnsureAsync(branch, commit, Redaction.Scrub(title),
                Redaction.Scrub(body) + $"\n\nCanonical operation: `{operationId}`\nCommit: `{commit}`\nScope digest: `{digest}`\nHuman review and canonical server approval remain required. No deployment executed.", cancellationToken);
            gitEvidence = Persist(gitEvidence with { PullRequestUrl = url });
            List<OperationBackendStep> backendSteps = [.. (proposal.BackendSteps ?? []),
                new("scoped-git-review", "host-configured-git", true, "Review branch and PR recorded.", commit, true)];
            return new OperationResponse("git-review-created", "Scoped configuration commit and review PR recorded; deployment awaits its canonical approval path.",
                [$"Commit: {commit}", $"Branch: {branch}", $"Pull request: {url}", $"Operation: {operationId}", $"Scope digest: {digest}"],
                ["Review the PR and canonical server proposal in the separate human inbox; no merge or deployment was issued."],
                ["configured base unchanged at admission", "staged paths within configured configuration scope", "non-force review-branch push", "canonical server operation recorded"], [],
                BackendSteps: backendSteps) { ServerOperations = proposal.ServerOperations, GitReview = gitEvidence };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OperationResponse("git-reconciliation-required", "Git/PR outcome requires reconciliation before retry; private process and provider errors were withheld.",
                [$"Deterministic review branch: {branch}", $"Scope digest: {digest}"],
                ["Inspect the existing canonical operation, remote branch and PR before retrying the same request."], [], [],
                BackendSteps: proposal?.BackendSteps) { ServerOperations = proposal?.ServerOperations ?? [], GitReview = gitEvidence };
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static ScopedGitReviewEvidence Persist(ScopedGitReviewEvidence evidence)
    {
        ProvisioningEvidenceStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "honua-devops", "provisioning", "evidence"));
        ProvisioningEvidenceReference receipt = store.Put("git-review", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evidence with { Receipt = null })));
        return evidence with { Receipt = receipt };
    }

    private bool Allowed(string value) => SafePath(value)
        && (value.StartsWith("desired-state/releases/", StringComparison.Ordinal)
            || value.StartsWith("desired-state/studio/", StringComparison.Ordinal)
            || value.StartsWith("studio/versions/", StringComparison.Ordinal))
        && (value.EndsWith(".json", StringComparison.Ordinal) || value.EndsWith(".yaml", StringComparison.Ordinal) || value.EndsWith(".yml", StringComparison.Ordinal))
        && configuration.AllowedPrefixes.Any(prefix => value.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal));
    private static bool SafePath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value)
        && !value.Contains('\\') && value.Split('/').All(part => part.Length > 0 && part is not "." and not ".." && !part.StartsWith('.'));
    private static string[] SplitPaths(string paths) => paths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private async Task<string> Git(string directory, string[] arguments, CancellationToken cancellationToken)
    {
        ProvisioningProcessResult result = await _runner.RunAsync(configuration.GitExecutable,
            ["-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false", "-c", "user.name=" + configuration.AuthorName, "-c", "user.email=" + configuration.AuthorEmail, .. arguments],
            directory, TimeSpan.FromMinutes(2), new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                // Stable author/committer timestamps make retries produce exactly the same commit.
                ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z", ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
            }, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException("Git operation failed.");
        return result.StandardOutput.TrimEnd();
    }
    private static OperationResponse Refusal(string status, string summary) => new(status, summary, [], [], [], []);
}
