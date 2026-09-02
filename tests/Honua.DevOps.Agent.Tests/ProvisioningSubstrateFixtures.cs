using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// Shared scaffolding for the provisioning tests.
/// </summary>
/// <remarks>
/// The seam under test is the process runner, so the honua-iac wrappers are faked —
/// but the DOCUMENTS they hand back are not. `fixtures/honua-iac/documents` holds
/// real output from one offline run of the real `terraform-exact-plan.sh` /
/// `terraform-exact-apply.sh` pair, and the schemas are honua-iac's own. That keeps
/// these tests honest about the shapes honua-devops must consume: nothing here is a
/// shape this repo invented for its own convenience.
/// </remarks>
internal static class ProvisioningSubstrateFixtures
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "honua-iac");

    internal static string ExactPlanMetadataJson => ReadFixture(Path.Combine("documents", "exact-plan-metadata.json"));

    internal static string ExecReceiptJson => ReadFixture(Path.Combine("documents", "exec-receipt.json"));

    internal static string TerraformOutputJson => ReadFixture(Path.Combine("documents", "terraform-output.json"));

    internal static string OperatorContractSchemaJson => ReadFixture(Path.Combine("contracts", "operator-contract.v1.schema.json"));

    internal static string ExactPlanSchemaJson => ReadFixture(Path.Combine("contracts", "terraform-exact-plan.v1.schema.json"));

    internal static string ExecReceiptSchemaJson => ReadFixture(Path.Combine("contracts", "terraform-exec-receipt.v1.schema.json"));

    /// <summary>The plan-metadata digest an approval for the fixture plan must bind.</summary>
    internal static string FixturePlanMetadataDigest
    {
        get
        {
            using JsonDocument document = JsonDocument.Parse(ExactPlanMetadataJson);
            return document.RootElement.GetProperty("plan_metadata_digest").GetString()!;
        }
    }

    /// <summary>The endpoint the fixture operator contract reports.</summary>
    internal const string ContractEndpoint = "https://honua.example.com";

    /// <summary>The admin-key locator the fixture operator contract reports.</summary>
    internal const string ContractAdminSecretRef =
        "arn:aws:secretsmanager:us-east-1:123456789012:secret:honuaecs-it-admin-password-AbCdEf";

    private static string ReadFixture(string relativePath)
    {
        string path = Path.Combine(FixtureRoot, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"honua-iac test fixture `{relativePath}` was not copied to the test output. "
                + "Check the csproj `fixtures/**/*.*` item.",
                path);
        }

        return File.ReadAllText(path);
    }

    internal const string ApprovalIssuer = "test://release-approver";

    internal static readonly byte[] ApprovalKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("honua-devops-test-approval-key"));

    /// <summary>The KMS key ARN the kms-mac tests sign and verify against.</summary>
    internal const string ApprovalKeyArn =
        "arn:aws:kms:us-east-1:123456789012:key/11111111-2222-3333-4444-555555555555";

    /// <summary>A second key, so "signed under a different key" is a real key, not a corrupted MAC.</summary>
    internal const string OtherApprovalKeyArn =
        "arn:aws:kms:us-east-1:123456789012:key/99999999-8888-7777-6666-555555555555";

    internal static OperationRuntime CreateRuntime(
        string iacRoot,
        ExecutionMode mode,
        ExecutionTier tier,
        string signingMode = ApprovalSigningModes.LocalHmacDev,
        IReadOnlyDictionary<string, string>? issuerKeyArns = null)
    {
        return new OperationRuntime(
            mode,
            tier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "trunk",
            TerraformLocalPath: iacRoot,
            TerraformDeploymentTargets: ["ecs"],
            ProductionEnvironments: ["prod"],
            ProvisionApprovalIssuerKeys: new Dictionary<string, string>
            {
                [ApprovalIssuer] = Convert.ToBase64String(ApprovalKey)
            },
            McpProxyPackage: "@honua/mcp-server@2026.1.1",
            McpProxyIntegrity: "sha512-dGVzdC1pbnRlZ3JpdHk=",
            CandidateReference: "honua-2026.1.1-test",
            ProvisionApprovalSigningMode: signingMode,
            ProvisionApprovalIssuerKeyArns: issuerKeyArns
                ?? new Dictionary<string, string>(StringComparer.Ordinal) { [ApprovalIssuer] = ApprovalKeyArn });
    }

    internal static OperatorPolicyModel DirectAllowedPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }

    /// <summary>
    /// Builds an approval receipt bound to the plan the toolkit just produced,
    /// including the plan-metadata digest the substrate enforces.
    /// </summary>
    internal static string CreateApprovalReceipt(
        OperationResponse plan,
        string action,
        string environment = "dev",
        string? overridePlanMetadataDigest = null,
        IApprovalSignatureProvider? signatureProvider = null,
        string? issuer = null,
        string? declaredSigningMode = null,
        bool omitSigningMode = false)
    {
        ProvisioningLineage lineage = Assert.IsType<ProvisioningLineage>(plan.ProvisioningLineage);
        DateTimeOffset issued = DateTimeOffset.UtcNow.AddSeconds(-1);
        DateTimeOffset expires = issued.AddMinutes(15);
        // Signs through the SAME provider the verifier uses, over the SAME canonical
        // payload helper. The fixture used to carry its own copy of the HMAC and the
        // field order; two copies of a signature scheme can drift apart without any
        // test noticing, which is exactly the bug a signing test cannot afford.
        IApprovalSignatureProvider provider = signatureProvider ?? LocalApprovalSignatureProvider();
        string issuerId = issuer ?? ApprovalIssuer;
        string keyId = provider.ResolveKeyId(issuerId)
            ?? throw new InvalidOperationException($"No key configured for approval issuer `{issuerId}`.");
        string receiptId = $"approval-{Guid.NewGuid():n}";
        string planMetadataDigest = overridePlanMetadataDigest ?? lineage.PlanMetadataDigest!;
        string signingMode = declaredSigningMode ?? provider.SigningMode;
        string canonical = ApprovalReceiptCanonicalization.Payload(
            "honua.devops.provision-approval/v1",
            receiptId,
            issuerId,
            keyId,
            lineage.ProvisioningOperationId,
            lineage.PlanSha256!,
            planMetadataDigest,
            action,
            "aws-ecs",
            environment,
            "approved",
            issued,
            expires,
            signingMode);
        ApprovalSignature signature = provider.SignAsync(issuerId, canonical).GetAwaiter().GetResult();

        Dictionary<string, object?> receipt = new()
        {
            ["schemaVersion"] = "honua.devops.provision-approval/v1",
            ["approvalReceiptId"] = receiptId,
            ["issuer"] = issuerId,
            ["keyId"] = keyId,
            ["provisioningOperationId"] = lineage.ProvisioningOperationId,
            ["planSha256"] = lineage.PlanSha256,
            ["planMetadataDigest"] = planMetadataDigest,
            ["action"] = action,
            ["stack"] = "aws-ecs",
            ["environment"] = environment,
            ["decision"] = "approved",
            ["signingMode"] = signingMode,
            ["issuedAtUtc"] = issued,
            ["expiresAtUtc"] = expires,
            ["signature"] = signature.Signature,
        };
        if (omitSigningMode)
        {
            receipt.Remove("signingMode");
        }

        return JsonSerializer.Serialize(receipt);
    }

    /// <summary>The development provider: one process holds the key, so it can both sign and verify.</summary>
    internal static IApprovalSignatureProvider LocalApprovalSignatureProvider()
        => new LocalHmacApprovalSignatureProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ApprovalIssuer] = Convert.ToBase64String(ApprovalKey)
        });

    internal static BackendGateway CreateGateway()
    {
        HttpClient client = new(new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        return new BackendGateway(
            new BackendConfiguration(
                new Uri("http://localhost:8080"),
                new Uri("http://localhost:4318"),
                null,
                null,
                "healthz/ready",
                "health",
                "v1/logs/search",
                "v1/metrics/search",
                "api/v1/admin/observability/errors",
                "api/v1/admin/observability/telemetry",
                "api/v1/metrics/health",
                "api/v1/metrics/performance",
                "api/v1/metrics/database",
                "api/v1/metrics/cache",
                "api/v1/metrics/memory",
                "api/v1/admin/performance/database/query-cache/statistics",
                "api/v1/admin/version",
                "api/v1/admin/capabilities",
                "api/v1/admin/manifest",
                "api/v1/admin/manifest/apply",
                TimeSpan.FromSeconds(5)),
            client);
    }

    internal static string ExtractChallenge(OperationResponse response, string marker)
    {
        string action = Assert.Single(response.Actions, value => value.Contains(marker, StringComparison.Ordinal));
        int start = action.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = action.IndexOf('.', start);
        return (end < 0 ? action[start..] : action[start..end]).Trim('`', ' ');
    }
}

/// <summary>
/// A synthetic honua-iac checkout: the wrapper scripts the substrate locator looks
/// for, the published contract schemas, and one qualified Terraform root.
/// </summary>
internal sealed class TerraformTestRoot : IDisposable
{
    internal TerraformTestRoot(
        bool withSubstrateScripts = true,
        bool withProviderLock = true,
        bool withContracts = true)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"honua-devops-provision-tests-{Guid.NewGuid():n}");
        AwsRoot = System.IO.Path.Combine(Path, "infrastructure", "terraform", "examples", "aws");
        Directory.CreateDirectory(AwsRoot);
        File.WriteAllText(System.IO.Path.Combine(AwsRoot, "main.tf"), "module \"honua\" {}\n");
        File.WriteAllText(
            System.IO.Path.Combine(AwsRoot, "variables.tf"),
            "variable \"honua_admin_password\" { sensitive = true }\n");
        File.WriteAllText(
            System.IO.Path.Combine(AwsRoot, "terraform.tfvars"),
            "# fixture: real secrets are never used\n");

        if (withProviderLock)
        {
            File.WriteAllText(
                System.IO.Path.Combine(AwsRoot, ".terraform.lock.hcl"),
                "provider \"registry.terraform.io/hashicorp/aws\" {\n  version = \"6.61.0\"\n}\n");
        }

        if (withSubstrateScripts)
        {
            string scripts = System.IO.Path.Combine(Path, "scripts");
            Directory.CreateDirectory(scripts);
            foreach (string name in new[]
            {
                "terraform-exact-plan.sh",
                "terraform-exact-apply.sh",
                "terraform-backend-identity.sh"
            })
            {
                // Existence is what the locator checks; the runner is faked, so these
                // are never executed by the tests.
                File.WriteAllText(System.IO.Path.Combine(scripts, name), "#!/usr/bin/env bash\nexit 0\n");
            }

            PlanScript = System.IO.Path.Combine(scripts, "terraform-exact-plan.sh");
            ApplyScript = System.IO.Path.Combine(scripts, "terraform-exact-apply.sh");
        }

        if (withContracts)
        {
            string contracts = System.IO.Path.Combine(Path, "infrastructure", "terraform", "contracts");
            Directory.CreateDirectory(contracts);
            File.WriteAllText(
                System.IO.Path.Combine(contracts, "operator-contract.v1.schema.json"),
                ProvisioningSubstrateFixtures.OperatorContractSchemaJson);
            File.WriteAllText(
                System.IO.Path.Combine(contracts, "terraform-exact-plan.v1.schema.json"),
                ProvisioningSubstrateFixtures.ExactPlanSchemaJson);
            File.WriteAllText(
                System.IO.Path.Combine(contracts, "terraform-exec-receipt.v1.schema.json"),
                ProvisioningSubstrateFixtures.ExecReceiptSchemaJson);
        }
    }

    internal string Path { get; }

    internal string AwsRoot { get; }

    internal string PlanScript { get; } = string.Empty;

    internal string ApplyScript { get; } = string.Empty;

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed record ProcessCall(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment)
{
    /// <summary>The wrapper script or terraform subcommand this call invoked.</summary>
    internal string Operation => FileName == "bash"
        ? System.IO.Path.GetFileName(Arguments[0])
        : Arguments[0];

    internal string? Option(string name)
    {
        for (int index = 0; index < Arguments.Count - 1; index++)
        {
            if (string.Equals(Arguments[index], name, StringComparison.Ordinal))
            {
                return Arguments[index + 1];
            }
        }

        return null;
    }
}

/// <summary>
/// Fakes the honua-iac wrappers by handing back the documents a real offline run of
/// them produced, so the consumption path is exercised against genuine shapes.
/// </summary>
internal sealed class FakeSubstrateRunner : IProvisioningProcessRunner
{
    internal List<ProcessCall> Calls { get; } = [];

    internal bool TerraformAvailable { get; set; } = true;

    /// <summary>When set, the plan wrapper refuses with this reason instead of planning.</summary>
    internal string? PlanRefusalReason { get; set; }

    /// <summary>When set, the apply wrapper refuses with this reason instead of applying.</summary>
    internal string? ApplyRefusalReason { get; set; }

    /// <summary>When set, the plan wrapper fails for a non-governed reason.</summary>
    internal string? PlanFailure { get; set; }

    internal string PlanSummary { get; set; } = "Plan: 7 to add, 0 to change, 0 to destroy.";

    internal string ShowOutput { get; set; } = TerraformShowOutput;

    internal string TerraformOutputJson { get; set; } = ProvisioningSubstrateFixtures.TerraformOutputJson;

    internal Func<Task>? BeforeApply { get; set; }

    internal int ApplyCalls { get; private set; }

    public bool CanRun(string fileName)
        => !string.Equals(fileName, "terraform", StringComparison.Ordinal) || TerraformAvailable;

    public async Task<ProvisioningProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        _ = timeout;
        cancellationToken.ThrowIfCancellationRequested();
        ProcessCall call = new(fileName, [.. arguments], workingDirectory, environment);
        Calls.Add(call);

        if (fileName == "bash")
        {
            string script = System.IO.Path.GetFileName(arguments[0]);
            return script switch
            {
                "terraform-exact-plan.sh" => RunPlan(call),
                "terraform-exact-apply.sh" => await RunApplyAsync(call, cancellationToken),
                _ => throw new InvalidOperationException($"Unexpected wrapper `{script}`.")
            };
        }

        if (fileName == "terraform")
        {
            return arguments[0] switch
            {
                "show" => Success(ShowOutput),
                "output" => Success(TerraformOutputJson),
                _ => throw new InvalidOperationException($"Unexpected terraform subcommand `{arguments[0]}`.")
            };
        }

        throw new InvalidOperationException($"Unexpected process `{fileName}`.");
    }

    private ProvisioningProcessResult RunPlan(ProcessCall call)
    {
        if (PlanRefusalReason is not null)
        {
            return Refusal(PlanRefusalReason);
        }

        if (PlanFailure is not null)
        {
            return new ProvisioningProcessResult(1, string.Empty, PlanFailure, false);
        }

        string planOut = call.Option("--plan-out")!;
        string metadataOut = call.Option("--metadata-out")!;
        File.WriteAllText(planOut, "fake saved terraform plan");
        File.WriteAllText(metadataOut, ProvisioningSubstrateFixtures.ExactPlanMetadataJson);
        return Success(PlanSummary);
    }

    private async Task<ProvisioningProcessResult> RunApplyAsync(ProcessCall call, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        if (BeforeApply is not null)
        {
            await BeforeApply().WaitAsync(cancellationToken);
        }

        if (ApplyRefusalReason is not null)
        {
            return Refusal(ApplyRefusalReason);
        }

        File.WriteAllText(call.Option("--receipt-out")!, ProvisioningSubstrateFixtures.ExecReceiptJson);
        return Success("Apply complete! Resources: 7 added, 0 changed, 0 destroyed.");
    }

    /// <summary>Reproduces the wrappers' refusal contract: stderr line plus exit 3.</summary>
    private static ProvisioningProcessResult Refusal(string reason)
        => new(
            TerraformExactRefusal.RefusedExitCode,
            string.Empty,
            $"[ERROR] REFUSED[{reason}]: the execution context moved between approval and execution",
            false);

    internal static ProvisioningProcessResult Success(string output)
        => new(0, output, string.Empty, false);

    // A representative `terraform show` rendering of a saved plan: one create, one
    // replacement, and one deletion, so the reviewable-evidence assertions cover the
    // destructive cases as well as the benign one.
    internal const string TerraformShowOutput = """
Terraform will perform the following actions:

  # aws_ecs_service.honua will be created
  + resource "aws_ecs_service" "honua" {
    }

  # aws_db_instance.honua must be replaced
  -/+ resource "aws_db_instance" "honua" {
    }

  # aws_s3_bucket.stale will be destroyed
  - resource "aws_s3_bucket" "stale" {
    }

Plan: 7 to add, 0 to change, 0 to destroy.
""";
}

internal sealed class FakeInstallHandoffVerifier(bool succeed) : IInstallHandoffVerifier
{
    internal InstallHandoffVerificationRequest? Request { get; private set; }

    public Task<InstallHandoffVerificationResult> VerifyAsync(
        InstallHandoffVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Request = request;
        OperationBackendStep step = new(
            "fake-full-handoff-verification", "mcp://fixture", succeed,
            succeed ? "all probes passed" : "roster missing", "<redacted>", false);
        return Task.FromResult(new InstallHandoffVerificationResult(
            succeed,
            succeed ? "install-handoff-verified" : "mcp-roster-incomplete",
            succeed ? "verified" : "missing required tool",
            succeed ? "server-fixture" : null,
            succeed ? request.RequiredTools : [],
            [step],
            succeed ? new string('b', 64) : null,
            succeed ? new string('c', 64) : null,
            succeed ? 137 : null,
            ChildReaped: succeed,
            SecretScanPassed: succeed));
    }
}
