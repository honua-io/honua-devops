namespace Honua.DevOps.Agent.Operations.DesiredState;

internal enum DriftIssueType
{
    // A required field is missing, mis-typed, mis-named, or otherwise violates
    // the object schema / naming conventions in desired-state/conventions.env.
    SchemaMismatch,

    // The object parses and matches schema but violates the operator control
    // model (policy/approval/promotion rules from the control contract).
    PolicyViolation,

    // A declared runtime/Terraform target is not in the validated allow-list.
    UnsupportedTarget
}

internal enum DriftSeverity
{
    Warning,
    Error
}

// One detected problem for one desired-state object, with a concrete suggested fix.
internal sealed record DriftIssue(
    DriftIssueType IssueType,
    DriftSeverity Severity,
    string Detail,
    string SuggestedFix,
    string? FieldPath = null);

// All issues detected for a single desired-state object (or file that failed to parse).
internal sealed record ObjectRemediation(
    string Path,
    string? Kind,
    string? Name,
    string? Namespace,
    IReadOnlyList<DriftIssue> Issues)
{
    internal bool IsClean => Issues.Count == 0;
}

// The structured drift-detection result. This is the typed result emitted by the
// detect_desired_state_drift tool; the OperationResponse carries a flattened,
// LLM-readable projection of it while the audit sink keeps the full object.
internal sealed record DesiredStateDriftReport(
    string DesiredStateRoot,
    int ObjectsScanned,
    int FilesFailedToParse,
    IReadOnlyList<ObjectRemediation> Remediations)
{
    internal IEnumerable<DriftIssue> AllIssues => Remediations.SelectMany(remediation => remediation.Issues);

    internal int IssueCount => AllIssues.Count();

    internal int SchemaMismatchCount => AllIssues.Count(issue => issue.IssueType == DriftIssueType.SchemaMismatch);

    internal int PolicyViolationCount => AllIssues.Count(issue => issue.IssueType == DriftIssueType.PolicyViolation);

    internal int UnsupportedTargetCount => AllIssues.Count(issue => issue.IssueType == DriftIssueType.UnsupportedTarget);

    internal bool HasErrors => AllIssues.Any(issue => issue.Severity == DriftSeverity.Error);

    internal bool IsClean => IssueCount == 0 && FilesFailedToParse == 0;
}
