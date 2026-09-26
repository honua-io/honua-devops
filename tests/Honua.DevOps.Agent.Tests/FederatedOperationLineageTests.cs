using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Tests;

public sealed partial class TerraformProvisioningTests
{
    [Fact]
    public async Task Federation_ApplyRetryAfterRestartReusesOriginalReceiptAndNeverReexecutes()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new();
        HonuaOperationsToolkit planner = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply");
        HonuaOperationsToolkit executor = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: runner);
        OperationResponse first = await executor.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true, challenge, approval);
        Assert.Equal("infrastructure-provisioned", first.Status);
        FakeSubstrateRunner restartedRunner = new();
        HonuaOperationsToolkit restarted = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv), gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), provisioningProcessRunner: restartedRunner);
        OperationResponse replay = await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true, challenge, approval);
        Assert.Equal("infrastructure-provisioned", replay.Status);
        Assert.Equal(first.ProvisioningLineage!.ProvisioningOperationId, replay.ProvisioningLineage!.ProvisioningOperationId);
        Assert.Equal(first.ProvisioningLineage.ActuatorReceiptReference, replay.ProvisioningLineage.ActuatorReceiptReference);
        Assert.Equal(first.ProvisioningLineage.ApplyAuditEventId, replay.ProvisioningLineage.ApplyAuditEventId);
        Assert.NotEqual(first.AuditEventId, replay.AuditEventId);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.Empty(restartedRunner.Calls);
        Assert.Equal("idempotency-conflict", (await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{}", true, challenge, "{}")).Status);
        Assert.Equal("idempotency-conflict", (await restarted.ProvisionInfrastructureAsync("aws-ecs", "small", "apply", "{\"environment\":\"prod\"}", true, challenge, approval)).Status);
        Assert.Empty(restartedRunner.Calls);
    }

    [Fact]
    public async Task Federation_PlanMetadataForDifferentBytesIsRejectedBeforeApply()
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        FakeSubstrateRunner runner = new() { PlanDocumentJson = ProvisioningSubstrateFixtures.ExactPlanMetadataJson.Replace("3a691c59f9c8be1eb1ce3e7642643a7aedfa7a0314c8d01750546ea83efca6fe", new string('0', 64), StringComparison.Ordinal) };
        HonuaOperationsToolkit planner = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway, provisioningProcessRunner: runner);
        OperationResponse result = await planner.ProvisionInfrastructureAsync("aws-ecs", "small", "plan", "{}", false, "");
        Assert.Equal("exact-plan-metadata-invalid", result.Status);
        Assert.Null(result.ProvisioningLineage);
        Assert.Equal(0, runner.ApplyCalls);
    }

    [Fact]
    public async Task Federation_PlanThroughVerifiedHandoffAndServerReplayRetainsExactEvidenceAndCanonicalIds()
    {
        using TerraformTestRoot root = new();
        using BackendGateway bootstrapGateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), bootstrapGateway, new FakeInstallHandoffVerifier(true));
        string provisioningId = apply.ProvisioningLineage!.ProvisioningOperationId;
        string directory = Path.Combine(root.Path, "federated");
        await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, provisioningId);
        Assert.Equal("install-handoff-verified", (await toolkit.VerifyInstallHandoffAsync(Path.Combine(directory, "honua-mcp-proxy.handoff.json"), false)).Status);

        // This fixture is specified independently of the client serializer. Distinct IDs
        // deliberately catch descriptor/invocation/proposal/audit/execution aliasing.
        string serverBytes = "{\n  \"operationId\":\"server-op-42\",\"operationInstanceId\":\"instance-23\","
            + "\"proposalId\":\"proposal-71\",\"auditId\":\"audit-52\",\"correlationId\":\"trace-86\","
            + "\"executionId\":\"exec-93\",\"providerOperationId\":\"ecs-deploy-15\",\"jobId\":\"job-64\","
            + "\"status\":\"AwaitingApproval\",\"evidenceRefs\":[\"honua://audit/audit-52\"],"
            + "\"decisionAudit\":{\"approvalId\":\"approval-32\"},\"target\":{\"parameters\":{"
            + "\"rootProvisioningOperationId\":\"" + provisioningId + "\"}}\n}\n";
        TestHttpMessageHandler handler = new(request => request.RequestUri!.AbsolutePath.Contains("/deploy/operations", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(serverBytes, Encoding.UTF8, "application/json") }
            : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        BackendConfiguration configuration = bootstrapGateway.Configuration with
        {
            HonuaApiBaseUri = new Uri(ProvisioningSubstrateFixtures.ContractEndpoint),
            RootProvisioningOperationId = provisioningId
        };
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Propose) with { DeployTargetId = "dev-api" };
        using HttpClient client = new(handler);
        using BackendGateway gateway = new(configuration, client);
        ConsoleOperationBridge bridge = new(runtime, gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy());
        OperationResponse created = await bridge.CreateGitOpsProposalAsync("roads-api", "dev", "v1.2.3", "sync", "ship", "operator");
        Assert.Equal("proposed", created.Status);
        Assert.Equal("proposal-71", created.ConsoleBridge!.Proposal!.ProposalId);
        CapturedRequest request = Assert.Single(handler.CapturedRequests, r => r.Method == "POST" && r.Uri.EndsWith("/deploy/operations", StringComparison.Ordinal));
        using JsonDocument create = JsonDocument.Parse(request.Body!);
        Assert.Equal(provisioningId, create.RootElement.GetProperty("parameters").GetProperty("rootProvisioningOperationId").GetString());
        Assert.False(create.RootElement.GetProperty("submitImmediately").GetBoolean());

        // A new gateway/bridge reads authority again; no local proposal store or identity.
        using BackendGateway restartedGateway = new(configuration, client);
        ConsoleOperationBridge restarted = new(runtime, restartedGateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy());
        OperationResponse read = await restarted.GetGitOpsProposalAsync("server-op-42");
        OperationResponse replay = await restarted.CreateGitOpsProposalAsync("roads-api", "dev", "v1.2.3", "sync", "ship", "operator");
        foreach (OperationResponse response in new[] { created, read, replay })
        {
            OperationResponse wire = JsonSerializer.Deserialize<OperationResponse>(JsonSerializer.Serialize(response))!;
            ServerOperationLineage server = Assert.Single(wire.ServerOperations);
            Assert.Equal("server-op-42", server.OperationId);
            Assert.Equal("instance-23", server.OperationInstanceId);
            Assert.Equal("proposal-71", server.ProposalId);
            Assert.Equal("audit-52", server.AuditId);
            Assert.Equal("trace-86", server.CorrelationId);
            Assert.Equal("exec-93", server.ExecutionId);
            Assert.Equal("job-64", server.JobId);
            Assert.Equal("ecs-deploy-15", server.ProviderOperationId);
            Assert.Equal("approval-32", server.DecisionAudit!.Value.GetProperty("approvalId").GetString());
            Assert.Equal("honua://audit/audit-52", server.EvidenceRefs!.Value[0].GetString());
            Assert.Equal(provisioningId, server.RootProvisioningOperationId);
            byte[] bytes = Encoding.UTF8.GetBytes(serverBytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), server.Receipt.Sha256);
            Assert.Equal(bytes, RetainedEvidence().Read(server.Receipt));
            RetainedEvidence().ValidateAppliedLineage(server.ProvisioningLineage!);
            Assert.Equal(8, server.ProvisioningLineage!.EvidenceRefs!.Count);
        }
        Assert.NotEqual(created.AuditEventId, replay.AuditEventId);
        string[] keys = handler.CapturedRequests.Where(r => r.Method == "POST" && r.Uri.EndsWith("/deploy/operations", StringComparison.Ordinal))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("idempotencyKey").GetString()!).ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Equal(keys[0], keys[1]);

        string auditPath = Path.Combine(root.Path, "server-audit.jsonl");
        await using (JsonlAuditSink sink = JsonlAuditSink.ForFile(auditPath))
            await ToolCallAuditor.EmitAsync(new("session", "plan", "propose", "pr-first", "mcp", sink),
                new("create_gitops_proposal", null), JsonSerializer.Serialize(created), CancellationToken.None);
        using JsonDocument audit = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
        Assert.Equal("proposal-71", audit.RootElement.GetProperty("serverOperations")[0].GetProperty("proposalId").GetString());
    }

    [Fact]
    public async Task Federation_RejectsUnknownOrWrongEndpointRootBeforeServerMutation()
    {
        using TerraformTestRoot root = new();
        using BackendGateway bootstrap = ProvisioningSubstrateFixtures.CreateGateway();
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { operationId = "unexpected" }));
        using HttpClient client = new(handler);
        using BackendGateway gateway = new(bootstrap.Configuration with { RootProvisioningOperationId = "urn:honua:provisioning:missing" }, client);
        ConsoleOperationBridge bridge = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Propose) with { DeployTargetId = "dev-api" }, gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy());
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.CreateGitOpsProposalAsync("roads-api", "dev", "v1.2.3", "sync", "ship", "operator"));
        Assert.DoesNotContain(handler.CapturedRequests, r => r.Uri.EndsWith("/deploy/operations", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("approvalReceiptId")]
    [InlineData("provisioningOperationId")]
    [InlineData("planSha256")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task Federation_RejectsSubstitutedJoinEvenWhenEvidenceBytesHaveValidDigest(string change)
    {
        using TerraformTestRoot root = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), gateway, new FakeInstallHandoffVerifier(true));
        OperationResponse handoff = await toolkit.InstallHandoffAsync("aws-ecs", "", "", Path.Combine(root.Path, "joined"), false, apply.ProvisioningLineage!.ProvisioningOperationId);
        ProvisioningLineage lineage = handoff.ProvisioningLineage!;
        List<ProvisioningEvidenceReference> references = [.. lineage.EvidenceRefs!];
        ProvisioningEvidenceStore store = RetainedEvidence();
        if (change == "missing") references.RemoveAll(r => r.Kind == "approval");
        else if (change == "duplicate") references.Add(references.Single(r => r.Kind == "approval"));
        else
        {
            var approval = System.Text.Json.Nodes.JsonNode.Parse(store.Read(references.Single(r => r.Kind == "approval")))!;
            approval[change] = "substituted";
            ProvisioningEvidenceReference replacement = store.Put("approval", Encoding.UTF8.GetBytes(approval.ToJsonString()));
            references.RemoveAll(r => r.Kind == "approval");
            references.Add(replacement);
            lineage = lineage with { ApprovalReceiptSha256 = replacement.Sha256 };
        }
        Assert.Throws<InvalidDataException>(() => store.ValidateAppliedLineage(lineage with { EvidenceRefs = references }));
    }

    [Fact]
    public async Task Federation_MetadataReleaseAndFindingReceiptsKeepServerIdsAndRequireTheVerifiedRoot()
    {
        using TerraformTestRoot root = new();
        (string provisioningId, BackendConfiguration configuration) = await VerifiedFederationAsync(root);
        string metadataBytes = "{"
            + "\"operationId\":\"metadata-op-7\",\"operationInstanceId\":\"metadata-instance-7\","
            + "\"proposalId\":\"metadata-proposal-7\",\"executionId\":\"metadata-exec-7\","
            + "\"rootProvisioningOperationId\":\"" + provisioningId + "\","
            + "\"metadataRelease\":{\"rootProvisioningOperationId\":\"" + provisioningId + "\"}}\n";
        string findingBytes = "{"
            + "\"findingId\":\"deploy-stuck-abc\",\"status\":\"ProposalCreated\","
            + "\"proposalId\":\"finding-proposal-4\",\"executionOperationId\":\"finding-exec-4\","
            + "\"rootProvisioningOperationId\":\"" + provisioningId + "\"}\n";
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/metadata/releases", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadataBytes, Encoding.UTF8, "application/json") };
            if (path.EndsWith("/propose", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(findingBytes, Encoding.UTF8, "application/json") };
            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        using HttpClient client = new(handler);
        using BackendGateway gateway = new(configuration, client);
        ActuationSpine spine = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Propose) with { DeployTargetId = "pkg-1" }, ProvisioningSubstrateFixtures.DirectAllowedPolicy());
        ActuationSpine.OperationGrant metadataGrant = spine.Authorize(new ActuationRequest(
            "honua.metadata-release.create", "create", "pkg-1", [], "field=name",
            "honua-devops:metadata-release:pkg-1", "proposal-required", true, "operator",
            BackendMutation.MetadataReleaseCreate)).Grant!;
        using BackendJsonResult created = await gateway.CreateMetadataReleaseOperationJsonAsync(
            "pkg-1", "dev", "roads", "name", "String", null, "add field", metadataGrant.IdempotencyKey, "corr-1", metadataGrant, CancellationToken.None);
        Assert.True(created.CallResult.IsSuccess);
        ServerOperationLineage metadata = created.CallResult.ServerLineage!;
        Assert.Equal("metadata-op-7", metadata.OperationId);
        Assert.Equal("metadata-instance-7", metadata.OperationInstanceId);
        Assert.Equal("metadata-proposal-7", metadata.ProposalId);
        Assert.Equal("metadata-exec-7", metadata.ExecutionId);
        Assert.Null(metadata.ExecutionOperationId);
        Assert.Equal(provisioningId, metadata.RootProvisioningOperationId);
        Assert.NotEqual(provisioningId, metadata.OperationId);

        using BackendGateway restarted = new(configuration, client);
        ActuationSpine.OperationGrant metadataRetry = spine.Authorize(new ActuationRequest(
            "honua.metadata-release.create", "create", "pkg-1", [], "field=name",
            "honua-devops:metadata-release:pkg-1", "proposal-required", true, "operator",
            BackendMutation.MetadataReleaseCreate)).Grant!;
        using BackendJsonResult replay = await restarted.CreateMetadataReleaseOperationJsonAsync(
            "pkg-1", "dev", "roads", "name", "String", null, "add field", metadataRetry.IdempotencyKey, "corr-1", metadataRetry, CancellationToken.None);
        Assert.Equal(metadata.OperationId, replay.CallResult.ServerLineage!.OperationId);
        Assert.Equal(metadata.ProposalId, replay.CallResult.ServerLineage.ProposalId);
        string[] metadataBodies = handler.CapturedRequests.Where(r => r.Method == "POST" && r.Uri.Contains("/metadata/releases/operations", StringComparison.Ordinal)).Select(r => r.Body!).ToArray();
        Assert.Equal(2, metadataBodies.Length);
        Assert.All(metadataBodies, body => Assert.Equal(provisioningId, JsonDocument.Parse(body).RootElement.GetProperty("rootProvisioningOperationId").GetString()));
        Assert.Equal(metadataBodies[0], metadataBodies[1]);

        HonuaOperationsToolkit toolkit = new(ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan), gateway);
        OperationResponse inspected = await toolkit.InspectMetadataReleaseAsync("pkg-1");
        Assert.Equal("in-progress", inspected.Status);
        ServerOperationLineage inspectedLineage = Assert.Single(JsonSerializer.Deserialize<OperationResponse>(JsonSerializer.Serialize(inspected))!.ServerOperations);
        Assert.Equal("metadata-op-7", inspectedLineage.OperationId);
        Assert.Equal(provisioningId, inspectedLineage.RootProvisioningOperationId);

        ActuationSpine.OperationGrant findingGrant = spine.Authorize(new ActuationRequest(
            "honua.ops-finding.propose", "propose", "deploy-stuck-abc", [], "finding=deploy-stuck-abc",
            "honua-devops:ops-finding:deploy-stuck-abc", "proposal-required", true, "operator",
            BackendMutation.OpsFindingPropose)).Grant!;
        using BackendJsonResult proposed = await gateway.ProposeOpsFindingAsync("deploy-stuck-abc", findingGrant, CancellationToken.None);
        using BackendJsonResult proposedAgain = await restarted.ProposeOpsFindingAsync("deploy-stuck-abc", findingGrant, CancellationToken.None);
        foreach (BackendJsonResult response in new[] { proposed, proposedAgain })
        {
            Assert.True(response.CallResult.IsSuccess);
            ServerOperationLineage finding = response.CallResult.ServerLineage!;
            Assert.Null(finding.OperationId);
            Assert.Equal("finding-proposal-4", finding.ProposalId);
            Assert.Equal("finding-exec-4", finding.ExecutionOperationId);
            Assert.NotEqual(finding.ExecutionOperationId, finding.OperationId);
            Assert.Equal(provisioningId, finding.RootProvisioningOperationId);
            Assert.Equal(provisioningId, finding.ProvisioningLineage!.ProvisioningOperationId);
        }
        string[] proposeBodies = handler.CapturedRequests.Where(r => r.Method == "POST" && r.Uri.EndsWith("/propose", StringComparison.Ordinal)).Select(r => r.Body!).ToArray();
        Assert.Equal(2, proposeBodies.Length);
        Assert.All(proposeBodies, body => Assert.Equal(provisioningId, JsonDocument.Parse(body).RootElement.GetProperty("rootProvisioningOperationId").GetString()));

        TestHttpMessageHandler missingRoot = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ProposalCreated\",\"proposalId\":\"proposal-x\",\"operationId\":\"" + provisioningId + "\"}\n", Encoding.UTF8, "application/json")
        });
        using HttpClient missingClient = new(missingRoot);
        using BackendGateway missingGateway = new(configuration, missingClient);
        using BackendJsonResult unproven = await missingGateway.ProposeOpsFindingAsync("deploy-stuck-abc", findingGrant, CancellationToken.None);
        Assert.False(unproven.CallResult.IsSuccess);
        Assert.True(unproven.CallResult.MutationAcknowledged);
        Assert.Null(unproven.CallResult.ServerLineage);
        Assert.Contains("lineage-evidence-invalid", unproven.CallResult.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("operationId", missingRoot.CapturedRequests[0].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Federation_CandidateReceiptJoinRejectsMissingMismatchedSubstitutedAndDuplicateBytes()
    {
        using TerraformTestRoot root = new();
        (string provisioningId, BackendConfiguration configuration) = await VerifiedFederationAsync(root);
        ProvisioningLineage lineage = HonuaOperationsToolkit.LoadVerifiedLineage(provisioningId, configuration.HonuaApiBaseUri);
        Assert.Null(lineage.ReleaseReceiptReference);
        Assert.Null(lineage.ReleaseReceiptSha256);
        string serverJson = "{\"operationId\":\"server-op-9\",\"operationInstanceId\":\"instance-9\",\"proposalId\":\"proposal-9\",\"auditId\":\"audit-9\",\"correlationId\":\"corr-9\",\"executionId\":\"exec-9\",\"rootProvisioningOperationId\":\"" + provisioningId + "\"}";
        byte[] serverBytes = Encoding.UTF8.GetBytes(serverJson);
        ProvisioningEvidenceStore store = RetainedEvidence();
        ProvisioningEvidenceReference serverReceipt = store.Put("server-operation", serverBytes);
        ServerOperationLineage server = ServerOperationLineage.Read(JsonDocument.Parse(serverJson).RootElement, serverReceipt);
        server = server with { ProvisioningLineage = lineage };
        Assert.Throws<InvalidDataException>(() => store.ValidateCandidateJoin(lineage, server, null));
        Assert.Null(lineage.ReleaseReceiptReference);

        string receiptJson = "{"
            + "\"provisioningOperationId\":\"" + lineage.ProvisioningOperationId + "\","
            + "\"planSha256\":\"" + lineage.PlanSha256 + "\","
            + "\"approvalReceiptId\":\"" + lineage.ApprovalReceiptId + "\","
            + "\"approvalReceiptSha256\":\"" + lineage.ApprovalReceiptSha256 + "\","
            + "\"applyAuditEventId\":\"" + lineage.ApplyAuditEventId + "\","
            + "\"actuatorReceiptReference\":\"" + lineage.ActuatorReceiptReference + "\","
            + "\"handoffReceiptSha256\":\"" + lineage.HandoffReceiptSha256 + "\","
            + "\"handoffVerificationReceiptId\":\"" + lineage.HandoffVerificationReceiptId + "\","
            + "\"rootProvisioningOperationId\":\"" + provisioningId + "\","
            + "\"serverOperationId\":\"server-op-9\","
            + "\"serverOperationInstanceId\":\"instance-9\","
            + "\"serverProposalId\":\"proposal-9\","
            + "\"serverAuditId\":\"audit-9\","
            + "\"serverCorrelationId\":\"corr-9\","
            + "\"serverExecutionId\":\"exec-9\""
            + "}";
        byte[] receiptBytes = Encoding.UTF8.GetBytes(receiptJson);
        ProvisioningEvidenceReference releaseReceipt = store.Put("release-receipt", receiptBytes);
        ProvisioningLineage joined = lineage with
        {
            ReleaseReceiptReference = releaseReceipt.Reference,
            ReleaseReceiptSha256 = releaseReceipt.Sha256
        };
        store.ValidateCandidateJoin(joined, server, receiptBytes);

        byte[] substituted = Encoding.UTF8.GetBytes(receiptJson.Replace("server-op-9", "server-op-other", StringComparison.Ordinal));
        ProvisioningEvidenceReference substitutedReceipt = store.Put("release-receipt", substituted);
        Assert.Throws<InvalidDataException>(() => store.ValidateCandidateJoin(
            joined with { ReleaseReceiptReference = substitutedReceipt.Reference, ReleaseReceiptSha256 = substitutedReceipt.Sha256 },
            server, substituted));
        Assert.Throws<InvalidDataException>(() => store.ValidateCandidateJoin(
            joined with { ReleaseReceiptSha256 = new string('0', 64) }, server, receiptBytes));
        byte[] duplicate = Encoding.UTF8.GetBytes(receiptJson.Replace("\"serverOperationId\"", "\"serverOperationId\":\"server-op-9\",\"serverOperationId\"", StringComparison.Ordinal));
        ProvisioningEvidenceReference duplicateReceipt = store.Put("release-receipt", duplicate);
        Assert.Throws<InvalidDataException>(() => store.ValidateCandidateJoin(
            joined with { ReleaseReceiptReference = duplicateReceipt.Reference, ReleaseReceiptSha256 = duplicateReceipt.Sha256 },
            server, duplicate));
    }

    private static async Task<(string ProvisioningId, BackendConfiguration Configuration)> VerifiedFederationAsync(TerraformTestRoot root)
    {
        using BackendGateway bootstrapGateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit toolkit, OperationResponse apply) = await ApplyAsync(root, new(), bootstrapGateway, new FakeInstallHandoffVerifier(true));
        string provisioningId = apply.ProvisioningLineage!.ProvisioningOperationId;
        string directory = Path.Combine(root.Path, "federated-" + Guid.NewGuid().ToString("n"));
        await toolkit.InstallHandoffAsync("aws-ecs", "", "", directory, false, provisioningId);
        Assert.Equal("install-handoff-verified", (await toolkit.VerifyInstallHandoffAsync(Path.Combine(directory, "honua-mcp-proxy.handoff.json"), false)).Status);
        BackendConfiguration configuration = bootstrapGateway.Configuration with
        {
            HonuaApiBaseUri = new Uri(ProvisioningSubstrateFixtures.ContractEndpoint),
            RootProvisioningOperationId = provisioningId
        };
        return (provisioningId, configuration);
    }
}

public sealed class ServerOperationLineageTests
{
    [Theory]
    [InlineData("{\"operationId\":\"a\",\"operationId\":\"b\"}")]
    [InlineData("{\"operationId\":\"a\",\"proposalId\":123}")]
    [InlineData("{\"operationId\":\"a\",\"rootProvisioningOperationId\":\"one\",\"parameters\":{\"rootProvisioningOperationId\":\"two\"}}")]
    public void RejectsDuplicateMalformedAndConflictingIds(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => ServerOperationLineage.Read(document.RootElement, new("server-operation", "unused", "unused", 0)));
    }

    [Fact]
    public void MissingUpstreamIdsRemainAbsentAndDescriptorIsNotInvocation()
    {
        using JsonDocument document = JsonDocument.Parse("{\"operationId\":\"admin.service.publish\"}");
        ServerOperationLineage lineage = ServerOperationLineage.Read(document.RootElement, new("server-operation", "unused", "unused", 0));
        Assert.Equal("admin.service.publish", lineage.OperationId);
        Assert.Null(lineage.OperationInstanceId);
        Assert.Null(lineage.ProposalId);
        Assert.Null(lineage.AuditId);
        Assert.Null(lineage.ExecutionId);
        Assert.Null(lineage.RootProvisioningOperationId);
    }
}
