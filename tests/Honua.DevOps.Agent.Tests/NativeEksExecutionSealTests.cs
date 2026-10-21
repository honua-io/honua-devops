using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class NativeEksExecutionSealTests
{
    private static readonly string Sha = new('a', 64);
    private static readonly string ReceiptSha = new('b', 64);
    private static readonly string OperatorSha = new('c', 64);

    [Fact]
    public void PostApplyStateAndExactConfiguredFactsAreSealedWithoutQualifyingProviderState()
    {
        var root = Path.Combine(Path.GetTempPath(), "honua-eks-seal-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            var store = new ProvisioningEvidenceStore(root);
            var lineage = Lineage();
            var execution = Execution();
            var facts = Facts();
            var seal = NativeEksExecutionSeal.Build(lineage, execution, ReceiptSha, OperatorSha, facts,
                "sha256:" + Sha, "https://honua.example.com", "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin");
            Assert.NotNull(seal);
            Assert.DoesNotContain("qualified", seal);
            var reference = store.Put("eks-execution-seal", Encoding.UTF8.GetBytes(seal!));
            lineage = lineage with { EvidenceRefs = [reference] };
            Assert.True(NativeEksExecutionSeal.Matches(store, lineage, execution, ReceiptSha, OperatorSha, facts,
                "sha256:" + Sha, "https://honua.example.com", "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin"));
            Assert.True(NativeEksExecutionSeal.Matches(new ProvisioningEvidenceStore(root), lineage, execution,
                ReceiptSha, OperatorSha, facts, "sha256:" + Sha, "https://honua.example.com",
                "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin"));
            Assert.False(NativeEksExecutionSeal.Matches(store, lineage,
                execution with { State = execution.State with { SerialAfter = 18 } },
                ReceiptSha, OperatorSha, facts, "sha256:" + Sha, "https://honua.example.com",
                "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin"));
            Assert.False(NativeEksExecutionSeal.Matches(store, lineage, execution, ReceiptSha,
                new string('e', 64), facts, "sha256:" + Sha, "https://honua.example.com",
                "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin"));
            Assert.False(NativeEksExecutionSeal.Matches(store, lineage, execution, ReceiptSha,
                OperatorSha, facts with { TargetId = "other" }, "sha256:" + Sha, "https://honua.example.com",
                "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin"));
            Assert.Null(NativeEksExecutionSeal.Build(lineage, execution with { ReleaseQualified = false },
                ReceiptSha, OperatorSha, facts, "sha256:" + Sha, "https://honua.example.com",
                "arn:aws:secretsmanager:us-west-2:123456789012:secret:admin"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ProvisioningLineage Lineage() => new("urn:honua:provisioning:1234567890abcdef1234567890abcdef",
        PlanMetadataDigest: Sha, ApprovalReceiptSha256: Sha, ActuatorReceiptReference: "urn:sha256:" + ReceiptSha);

    private static IacExecutionEvidence Execution() => new(
        PlanMetadataDigest: Sha, SavedPlanSha256: Sha, TerraformRoot: "aws-eks-native",
        IacRevision: new string('d', 40), IacTreeDigest: Sha, TerraformVersion: "1.14.0",
        ProviderLockDigest: Sha, InputDigest: Sha,
        Backend: new IacBackendIdentity(Sha, "s3", true, "default", "cells/eks.tfstate", "us-west-2"),
        ExecutionIdentity: new IacExecutionIdentity("arn:aws:sts::123456789012:assumed-role/executor/session",
            "AROATEST", "123456789012", "aws", "issuer", "sts-assumed-role"),
        State: new IacStateLineage(null, null, Guid.NewGuid().ToString(), 17),
        OperatorContractDigest: OperatorSha, EvidenceMode: "live", ReleaseQualified: true);

    private static ProvisionedRuntimeFacts Facts() => new("aws-eks", "Kubernetes", "honua-kubernetes-deployment",
        "eks-dev", "dev", "123456789012", "us-west-2", "aws", JsonSerializer.SerializeToElement(new
        {
            clusterArn = "arn:aws:eks:us-west-2:123456789012:cluster/honua-dev",
            deploymentName = "honua-server", serviceName = "honua-server-public"
        }));
}
