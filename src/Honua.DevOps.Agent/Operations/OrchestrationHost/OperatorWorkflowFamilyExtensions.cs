namespace Honua.DevOps.Agent.Operations.OrchestrationHost;

internal static class OperatorWorkflowFamilyExtensions
{
    internal static OperatorWorkflowFamily Parse(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "analyze"
            : value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "analyze" or "analysis" or "analyst" => OperatorWorkflowFamily.Analyze,
            "publish" or "publishing" or "publish-data" or "publish_data" => OperatorWorkflowFamily.Publish,
            "build" or "builder" or "build-app" or "build_app" => OperatorWorkflowFamily.Build,
            "deploy" or "deployment" or "automate" or "automate-deploy" or "automate_deploy" => OperatorWorkflowFamily.Deploy,
            _ => throw new InvalidOperationException(
                $"Invalid operator workflow family `{value}`. Allowed values: analyze, publish, build, deploy.")
        };
    }

    internal static string ToConfigValue(this OperatorWorkflowFamily family)
    {
        return family switch
        {
            OperatorWorkflowFamily.Analyze => "analyze",
            OperatorWorkflowFamily.Publish => "publish",
            OperatorWorkflowFamily.Build => "build",
            OperatorWorkflowFamily.Deploy => "deploy",
            _ => throw new InvalidOperationException("Unsupported operator workflow family.")
        };
    }
}
