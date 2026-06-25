using System.Net.Http;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Tests;

public partial class ConsoleOperationBridgeTests
{
    [Fact]
    public async Task PlanGpProvisionAsync_ValidFargateProfile_RecordsPlanFirstProposalWithoutExecuting()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-gp-1", status = "AwaitingApproval" });
            }

            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.PlanGpProvisionAsync(
            service: "gp-clip",
            environmentsCsv: "dev",
            vcpus: 4,
            memoryMib: 16384,
            gpuCount: 0,
            timeoutSeconds: 7200,
            architecture: "arm64",
            image: "",
            ephemeralStorageGib: 100,
            retryAttempts: 2,
            computePlatform: "fargate-spot",
            owner: "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Equal("op-gp-1", proposal.OperationId);

        // The durable operation is recorded with submitImmediately=false (advisory, never executes).
        CapturedRequest createRequest = Assert.Single(
            handler.CapturedRequests,
            request => request.Method == "POST" && request.Uri.EndsWith("/deploy/operations", StringComparison.Ordinal));
        using JsonDocument body = JsonDocument.Parse(createRequest.Body!);
        Assert.False(body.RootElement.GetProperty("submitImmediately").GetBoolean());

        // No terraform apply, manifest apply, submit, or rollback from the plan path.
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/manifest/apply", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/rollback", StringComparison.Ordinal));

        // The GP terraform var mapping is surfaced with the exact honua-iac variable names.
        Assert.Contains(response.Findings, f => f.Contains("enable_gp_batch = true", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("gp_batch_cpu_architecture = ARM64", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("gp_batch_ephemeral_storage_gib = 100", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("gp_batch_vcpus = 4", StringComparison.Ordinal));
        // Plan-first: the step plan shows terraform plan, never apply, in plan mode.
        Assert.Contains(response.Findings, f => f.Contains("plan-first", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("Plan-infra command:", StringComparison.Ordinal) && f.Contains(" plan ", StringComparison.Ordinal));
        Assert.DoesNotContain(response.Findings, f => f.Contains("Plan-infra command:", StringComparison.Ordinal) && f.Contains(" apply ", StringComparison.Ordinal));
        Assert.Contains("gp-apply-mode:plan-only", response.ValidationChecks);
    }

    [Fact]
    public async Task PlanGpProvisionAsync_GpuOnFargate_IsBlockedAndRecordsNothing()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.PlanGpProvisionAsync(
            service: "gp-ml-raster",
            environmentsCsv: "dev",
            vcpus: 8,
            memoryMib: 32768,
            gpuCount: 1,
            timeoutSeconds: 7200,
            architecture: "x86_64",
            image: "",
            ephemeralStorageGib: 0,
            retryAttempts: 1,
            computePlatform: "fargate-spot",
            owner: "soleil");

        // Blocked projection: no proposal, no operation, no backend write at all.
        Assert.Equal("gp-profile-rejected", response.Status);
        Assert.Contains("gp-profile-valid:false", response.ValidationChecks);
        Assert.Contains("gp-no-operation-recorded", response.ValidationChecks);
        Assert.Contains(response.Risks, r => r.Contains("EC2", StringComparison.Ordinal));

        // Critically: the invalid profile mints NOTHING — no deploy-control call whatsoever.
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/deploy/operations", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/deploy/plan", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/deploy/preflight", StringComparison.Ordinal));
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task PlanGpProvisionAsync_NoDeployTarget_StaysBlockedWithoutInventingOperation()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: null);

        OperationResponse response = await bridge.PlanGpProvisionAsync(
            service: "gp-clip",
            environmentsCsv: "dev",
            vcpus: 2,
            memoryMib: 4096,
            gpuCount: 0,
            timeoutSeconds: 3600,
            architecture: "x86_64",
            image: "",
            ephemeralStorageGib: 0,
            retryAttempts: 1,
            computePlatform: "fargate-spot",
            owner: "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Null(proposal.OperationId);
        // The valid profile still surfaces the GP mapping even when the target is unconfigured.
        Assert.Contains(response.Findings, f => f.Contains("enable_gp_batch = true", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
    }
}
