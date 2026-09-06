using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
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
