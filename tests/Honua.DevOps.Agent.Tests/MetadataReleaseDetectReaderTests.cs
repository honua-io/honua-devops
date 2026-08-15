using System.Text.Json;
using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// Coverage for the metadata-release detect helpers the DevOps AI loop uses to confirm the
// safe-rollback closed loop fired (honua-server #1738/#1739) before it diagnoses and proposes a
// human-approved resolve. The shape mirrors the server's DeployOperationResponse for a
// WorkflowOperationKind.MetadataRelease operation.
public sealed class MetadataReleaseDetectReaderTests
{
    private const string RolledBackOperationJson = """
    {
      "operationId": "metadata-release-pkg-demo-b",
      "kind": "MetadataRelease",
      "status": "RolledBack",
      "currentPhase": "Reversible rollback complete (ScriptRollback): reactivated prior revision and executed the inverse script.",
      "errorMessage": null,
      "metadataRelease": {
        "packageId": "pkg-demo-b",
        "currentStage": "RollbackRequested",
        "rollbackPlan": { "class": "ScriptRollback", "isDataAffecting": true },
        "evidenceRefs": [ { "kind": "smoke", "refId": "smoke:metadata-release-pkg-demo-b" } ]
      }
    }
    """;

    private const string SucceededOperationJson = """
    {
      "operationId": "metadata-release-pkg-ok",
      "status": "Succeeded",
      "currentPhase": "Smoke check passed: 7071 row(s) returned and field 'owner_email' is present.",
      "metadataRelease": {
        "packageId": "pkg-ok",
        "currentStage": "Complete",
        "evidenceRefs": [ { "kind": "smoke" } ]
      }
    }
    """;

    [Fact]
    public void Reads_RolledBack_MetadataRelease_WithSmokeEvidence()
    {
        using JsonDocument document = JsonDocument.Parse(RolledBackOperationJson);
        JsonElement root = document.RootElement;

        Assert.Equal("RolledBack", DeployOperationReader.ReadStatus(root));
        Assert.True(DeployOperationReader.IsRolledBack(DeployOperationReader.ReadStatus(root)));
        Assert.Equal("RollbackRequested", DeployOperationReader.ReadMetadataReleaseStage(root));
        Assert.Equal("ScriptRollback", DeployOperationReader.ReadRollbackClass(root));
        Assert.True(DeployOperationReader.ReadRollbackIsDataAffecting(root));
        Assert.True(DeployOperationReader.HasSmokeEvidence(root));
        Assert.Contains("Reversible rollback complete", DeployOperationReader.ReadCurrentPhase(root));
    }

    [Fact]
    public void Reads_Succeeded_MetadataRelease()
    {
        using JsonDocument document = JsonDocument.Parse(SucceededOperationJson);
        JsonElement root = document.RootElement;

        Assert.True(DeployOperationReader.IsSuccess(DeployOperationReader.ReadStatus(root)));
        Assert.False(DeployOperationReader.IsRolledBack(DeployOperationReader.ReadStatus(root)));
        Assert.Equal("Complete", DeployOperationReader.ReadMetadataReleaseStage(root));
        Assert.True(DeployOperationReader.HasSmokeEvidence(root));
    }

    [Fact]
    public void DetectsManualInterventionRequired_ForDeferredSnapshotRollback()
    {
        const string manualJson = """
        {
          "status": "ManualInterventionRequired",
          "errorMessage": "Snapshot rollback is not yet implemented; operator-managed recovery is required.",
          "metadataRelease": { "currentStage": "Failed", "rollbackPlan": { "class": "SnapshotRestore" } }
        }
        """;
        using JsonDocument document = JsonDocument.Parse(manualJson);
        JsonElement root = document.RootElement;

        Assert.True(DeployOperationReader.IsManualInterventionRequired(DeployOperationReader.ReadStatus(root)));
        Assert.Contains("Snapshot rollback is not yet implemented", DeployOperationReader.ReadErrorMessage(root));
        Assert.False(DeployOperationReader.HasSmokeEvidence(root));
    }
}
