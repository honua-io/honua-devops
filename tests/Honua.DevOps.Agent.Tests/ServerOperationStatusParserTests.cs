using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// Locks the single canonical recognizer for the honua-server WorkflowOperationStatus
// vocabulary: it must fold every casing/hyphenation onto one member, and the previously
// independent parse sites (DeployOperationReader, the Console bridge mappings) must all read
// the contract through it so they cannot drift apart.
public sealed class ServerOperationStatusParserTests
{
    // Expected values are the enum member NAMES (the enum is internal, so InlineData carries
    // strings and the assertion compares ToString()).
    [Theory]
    [InlineData("planned", "Planned")]
    [InlineData("Planned", "Planned")]
    [InlineData("awaitingapproval", "AwaitingApproval")]
    [InlineData("AwaitingApproval", "AwaitingApproval")]
    [InlineData("awaiting-approval", "AwaitingApproval")]
    [InlineData("  Awaiting-Approval  ", "AwaitingApproval")]
    [InlineData("submitted", "Submitted")]
    [InlineData("reconciling", "Reconciling")]
    [InlineData("succeeded", "Succeeded")]
    [InlineData("failed", "Failed")]
    [InlineData("rollbackrequested", "RollbackRequested")]
    [InlineData("rollback-requested", "RollbackRequested")]
    [InlineData("rolledback", "RolledBack")]
    [InlineData("rolled-back", "RolledBack")]
    [InlineData("manualinterventionrequired", "ManualInterventionRequired")]
    [InlineData("manual-intervention-required", "ManualInterventionRequired")]
    public void Recognize_FoldsCasingAndHyphenation(string raw, string expected)
        => Assert.Equal(expected, ServerOperationStatusParser.Recognize(raw).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-status")]
    [InlineData("rejected")] // devops-side decision terminal, never a server enum member
    public void Recognize_UnknownInputIsUnrecognized(string? raw)
        => Assert.Equal("Unrecognized", ServerOperationStatusParser.Recognize(raw).ToString());

    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("failed", true)]
    [InlineData("rolled-back", true)]
    [InlineData("manual-intervention-required", true)]
    [InlineData("planned", false)]
    [InlineData("awaiting-approval", false)]
    [InlineData("submitted", false)]
    [InlineData("reconciling", false)]
    [InlineData("rollback-requested", false)]
    [InlineData("unknown-status", false)]
    [InlineData(null, false)]
    public void IsTerminal_MatchesServerTerminalStatuses(string? status, bool expected)
        => Assert.Equal(expected, ServerOperationStatusParser.IsTerminal(status));

    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("Succeeded", true)]
    [InlineData("failed", false)]
    [InlineData(null, false)]
    public void IsSuccess_OnlyMatchesSucceeded(string? status, bool expected)
        => Assert.Equal(expected, ServerOperationStatusParser.IsSuccess(status));

    [Theory]
    [InlineData("rolledback", true)]
    [InlineData("rolled-back", true)]
    [InlineData("succeeded", false)]
    public void IsRolledBack_MatchesEitherForm(string status, bool expected)
        => Assert.Equal(expected, ServerOperationStatusParser.IsRolledBack(status));

    [Theory]
    [InlineData("awaitingapproval", true)]
    [InlineData("awaiting-approval", true)]
    [InlineData("submitted", false)]
    public void IsAwaitingApproval_MatchesEitherForm(string status, bool expected)
        => Assert.Equal(expected, ServerOperationStatusParser.IsAwaitingApproval(status));

    // The whole point of the consolidation: every parse site must agree with the canonical
    // recognizer on the same input. If a future change re-forks one of them, these assertions
    // fail before the drift can ship.
    [Theory]
    [InlineData("succeeded")]
    [InlineData("rolled-back")]
    [InlineData("ManualInterventionRequired")]
    public void DeployOperationReader_AgreesWithCanonicalParser(string status)
    {
        Assert.Equal(ServerOperationStatusParser.IsTerminal(status), DeployOperationReader.IsTerminal(status));
        Assert.Equal(ServerOperationStatusParser.IsSuccess(status), DeployOperationReader.IsSuccess(status));
        Assert.Equal(ServerOperationStatusParser.IsRolledBack(status), DeployOperationReader.IsRolledBack(status));
        Assert.Equal(
            ServerOperationStatusParser.IsManualInterventionRequired(status),
            DeployOperationReader.IsManualInterventionRequired(status));
    }
}
