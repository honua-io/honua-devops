using System.Net;
using System.Text;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public sealed class ScopedGitOpsAdapterTests
{
    private const string Package = """
    {"releaseId":"review-1","service":"roads","desiredRevision":"v1.0.0","targetEnvironments":["dev"],
     "semanticResources":[{"kind":"StudioVersion","name":"map-v1","action":"upsert"}],
     "compatibility":{"status":"compatible","breakingChanges":0},
     "scriptCoverage":{"covered":0,"total":0},"rollbackPolicy":{"classification":"not-required"}}
    """;

    [Fact]
    public async Task RealGit_CommitsPushesReplaysAndRevertsOnlyReviewBranches_WithoutDeploying()
    {
        string root = Directory.CreateTempSubdirectory("honua-scoped-git-test-").FullName;
        try
        {
            string remote = Path.Combine(root, "remote.git");
            string seed = Path.Combine(root, "seed");
            await Git(root, "init", "--bare", remote);
            await Git(root, "clone", remote, seed);
            await Git(seed, "checkout", "-b", "trunk");
            await File.WriteAllTextAsync(Path.Combine(seed, "notes.md"), "unrelated user content\n");
            await Git(seed, "add", ".");
            await Git(seed, "commit", "-m", "Initial unrelated content");
            await File.WriteAllTextAsync(Path.Combine(seed, "private-feature.geojson"), "{\"type\":\"FeatureCollection\",\"features\":[]}\n");
            await Git(seed, "add", ".");
            await Git(seed, "commit", "-m", "User source feature outside configuration scope");
            await Git(seed, "push", "origin", "trunk");
            string initial = await Git(seed, "rev-parse", "HEAD");
            List<string> requests = [];
            using HttpClient http = new(new ProposalHandler(requests));
            using BackendGateway gateway = new(BackendConfiguration.Load() with { RootProvisioningOperationId = null }, http);
            OperatorPolicyModel policy = OperatorPolicyModel.Default with { AuditHookTarget = new Uri(Path.Combine(root, "audit.jsonl")).AbsoluteUri };
            OperationRuntime runtime = OperationRuntime.SafeDefault with
            { ExecutionMode = ExecutionMode.Execute, ExecutionTier = ExecutionTier.Propose, DeployTargetId = "ecs-review-target" };
            PullRequests prs = new();
            ScopedGitOpsAdapter adapter = new(runtime, gateway, policy,
                new(remote, "honua-io/fixture", "trunk", ["desired-state/releases/"], null), pullRequests: prs);

            Assert.Equal("git-scope-rejected", (await adapter.RevertAsync(initial, initial, "roads", "dev")).Status);
            Assert.Empty(requests);
            OperationResponse created = await adapter.CreateAsync(Package, initial);
            Assert.Equal("git-review-created", created.Status);
            Assert.NotNull(created.GitReview);
            Assert.Equal("honua.devops.git-review/v1", created.GitReview.SchemaVersion);
            Assert.NotNull(created.GitReview.Receipt);
            Assert.True(created.GitReview.RemoteMutationAcknowledged);
            string commit = created.Findings.Single(value => value.StartsWith("Commit: ", StringComparison.Ordinal))[8..];
            string branch = created.Findings.Single(value => value.StartsWith("Branch: ", StringComparison.Ordinal))[8..];
            Assert.Equal(initial, await Git(root, "--git-dir", remote, "rev-parse", "refs/heads/trunk"));
            Assert.Equal(commit, await Git(root, "--git-dir", remote, "rev-parse", $"refs/heads/{branch}"));
            Assert.Contains("Canonical operation:", prs.Body);
            Assert.All(requests, path => Assert.DoesNotContain("/submit", path));
            Assert.All(requests, path => Assert.DoesNotContain("/rollback", path));

            OperationResponse replay = await adapter.CreateAsync(Package, initial);
            Assert.Equal("git-review-created", replay.Status);
            Assert.Contains($"Commit: {commit}", replay.Findings);
            // Simulate separate human-reviewed merge on this isolated bare fixture only.
            await Git(root, "--git-dir", remote, "update-ref", "refs/heads/trunk", commit, initial);
            Assert.Equal("git-base-conflict", (await adapter.CreateAsync(Package, initial)).Status);
            OperationResponse reverted = await adapter.RevertAsync(commit, commit, "roads", "dev");
            Assert.Equal("git-review-created", reverted.Status);
            string revertedCommit = reverted.Findings.Single(value => value.StartsWith("Commit: ", StringComparison.Ordinal))[8..];
            Assert.Equal("", await Git(root, "--git-dir", remote, "diff", "--name-only", initial, revertedCommit));
            Assert.Equal(commit, await Git(root, "--git-dir", remote, "rev-parse", "refs/heads/trunk"));
            Assert.Equal("unrelated user content", await Git(root, "--git-dir", remote, "show", $"{revertedCommit}:notes.md"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DefaultPlanAndInvalidScopeRefuseBeforeRemoteMutation()
    {
        using BackendGateway gateway = new(BackendConfiguration.Load() with { RootProvisioningOperationId = null }, new HttpClient(new ProposalHandler([])));
        ScopedGitConfiguration configuration = new("/nonexistent", "honua-io/fixture", "trunk", ["desired-state/releases/"], null);
        ScopedGitOpsAdapter adapter = new(OperationRuntime.SafeDefault with { DeployTargetId = "target" }, gateway, OperatorPolicyModel.Default, configuration);
        Assert.Equal("git-plan-only", (await adapter.CreateAsync(Package, new string('a', 40))).Status);
        Assert.Equal("git-revert-rejected", (await adapter.RevertAsync("HEAD", new string('a', 40), "roads", "dev")).Status);
        ScopedGitOpsAdapter narrow = new(OperationRuntime.SafeDefault, gateway, OperatorPolicyModel.Default,
            configuration with { AllowedPrefixes = ["desired-state/studio/"] });
        Assert.Equal("git-scope-rejected", (await narrow.CreateAsync(Package, new string('a', 40))).Status);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        ProvisioningProcessResult result = await SystemProvisioningProcessRunner.Instance.RunAsync("git",
            ["-c", "user.name=Mike McDougall", "-c", "user.email=mike@honua.io", .. arguments], directory, TimeSpan.FromSeconds(20));
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private sealed class PullRequests : IScopedGitPullRequestClient
    {
        internal string Body = "";
        public Task<string> EnsureAsync(string branch, string expectedCommit, string title, string body, CancellationToken cancellationToken)
        { Body = body; return Task.FromResult("https://github.com/honua-io/fixture/pull/1"); }
    }
    private sealed class ProposalHandler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            string body = request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/deploy/operations", StringComparison.Ordinal)
                ? """{"operationId":"canonical-review-op","status":"AwaitingApproval","auditId":"audit-ref"}""" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
