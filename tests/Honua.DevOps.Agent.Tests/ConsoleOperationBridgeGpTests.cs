using System.Net.Http;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;

namespace Honua.DevOps.Agent.Tests;

public partial class ConsoleOperationBridgeTests
{
    [Fact]
    public async Task PlanGpSubstrateAsync_RecordsPlanFirstProposalWithoutExecuting_AndBindsToOutputs()
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

        OperationResponse response = await bridge.PlanGpSubstrateAsync(
            service: "geoprocessing",
            environmentsCsv: "dev",
            architecture: "arm64",
            image: "",
            maxVcpus: 512,
            createWorkerGdalRepo: true,
            tiersCsv: "",
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

        // Substrate inputs are surfaced (per-ENV, not per-job).
        Assert.Contains(response.Findings, f => f.Contains("enable_gp_batch = true", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("gp_batch_cpu_architecture = ARM64", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("gp_batch_max_vcpus = 512", StringComparison.Ordinal));
        // The tier pool is hardcoded in honua-iac; no tiers var is surfaced as a substrate input.
        Assert.DoesNotContain(response.Findings, f => f.Contains("gp_job_definition_tiers", StringComparison.Ordinal));

        // No per-job profile knobs leak into the substrate proposal.
        Assert.DoesNotContain(response.Findings, f => f.Contains("gp_batch_vcpus", StringComparison.Ordinal));

        // Binds to substrate OUTPUTS (ARNs), not input-variable names.
        Assert.Contains(response.Findings, f => f.Contains("output gp_job_queue_arn", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("output gp_job_definition_arns", StringComparison.Ordinal));

        // Plan-first: the step plan shows terraform plan, never apply, in plan mode.
        Assert.Contains(response.Findings, f => f.Contains("plan-first", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("Plan-infra command:", StringComparison.Ordinal) && f.Contains(" plan ", StringComparison.Ordinal));
        Assert.DoesNotContain(response.Findings, f => f.Contains("Plan-infra command:", StringComparison.Ordinal) && f.Contains(" apply ", StringComparison.Ordinal));
        Assert.Contains("gp-apply-mode:plan-only", response.ValidationChecks);
    }

    [Fact]
    public async Task PlanGpSubstrateAsync_NoDeployTarget_StaysBlockedWithoutInventingOperation()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: null);

        OperationResponse response = await bridge.PlanGpSubstrateAsync(
            service: "geoprocessing",
            environmentsCsv: "dev",
            architecture: "x86_64",
            image: "",
            maxVcpus: 0,
            createWorkerGdalRepo: true,
            tiersCsv: "",
            owner: "soleil");

        GitOpsProposalBridge proposal = AssertProposal(response);
        Assert.Null(proposal.OperationId);
        // The substrate mapping still surfaces even when the target is unconfigured.
        Assert.Contains(response.Findings, f => f.Contains("enable_gp_batch = true", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.Contains("/submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanGpJobSizingAsync_IsPureRuntimeAid_RecordsNothing_SelectsTierAndOverrides()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.PlanGpJobSizingAsync(
            vcpus: 8,
            memoryMib: 32768,
            timeoutSeconds: 7200,
            retryAttempts: 3,
            ephemeralStorageGib: 100,
            gpuCount: 0);

        // Pure aid: NO backend call whatsoever (no terraform, no deploy-control operation).
        Assert.Empty(handler.CapturedRequests);

        Assert.Equal("gp-job-sizing", response.Status);
        Assert.Contains("gp-sizing-no-terraform", response.ValidationChecks);
        Assert.Contains("gp-sizing-no-operation-recorded", response.ValidationChecks);
        // ephemeral=100 selects the `l` tier; overrides carry vCPU/memory/timeout/retry/ephemeral.
        Assert.Contains("gp-sizing-tier:l", response.ValidationChecks);
        Assert.Contains(response.Findings, f => f.Contains("batch.vcpus = 8", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("batch.memory_mib = 32768", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("batch.ephemeral_gib = 100", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("gp_job_definition_arns.l", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanGpJobSizingAsync_Gpu_IsAdvisoryNote_StillValid()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.PlanGpJobSizingAsync(
            vcpus: 8,
            memoryMib: 32768,
            timeoutSeconds: 7200,
            retryAttempts: 1,
            ephemeralStorageGib: 0,
            gpuCount: 1);

        Assert.Empty(handler.CapturedRequests);
        // GPU is a note, not a hard reject — the sizing still resolves a tier.
        Assert.Equal("gp-job-sizing", response.Status);
        Assert.Contains("gp-job-sizing-valid:true", response.ValidationChecks);
        Assert.Contains(response.Risks, r => r.Contains("GPU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanGpJobSizingAsync_AboveFargateCeiling_IsRejected()
    {
        TestHttpMessageHandler handler = new(request => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        ConsoleOperationBridge bridge = CreateBridge(handler, deployTargetId: "prod-api");

        OperationResponse response = await bridge.PlanGpJobSizingAsync(
            vcpus: 4,
            memoryMib: 8192,
            timeoutSeconds: 3600,
            retryAttempts: 1,
            ephemeralStorageGib: 250,
            gpuCount: 0);

        Assert.Empty(handler.CapturedRequests);
        Assert.Equal("gp-job-sizing-rejected", response.Status);
        Assert.Contains("gp-job-sizing-valid:false", response.ValidationChecks);
        Assert.Contains("gp-sizing-tier:none", response.ValidationChecks);
    }
}
