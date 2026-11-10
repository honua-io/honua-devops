using System.Text.RegularExpressions;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class DeploymentSkillTests
{
    [Fact]
    public void DeploymentSkillReferencesOnlyExposedOperatorTools()
    {
        string skill = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "skills", "honua-deploy-aws", "SKILL.md"));
        string[] referencedTools = Regex.Matches(skill, "`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        var exposedTools = CapabilityToolset.Create(OperationRuntime.SafeDefault, gateway)
            .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("install_honua", referencedTools);
        Assert.All(referencedTools, name => Assert.Contains(name, exposedTools));
    }
}
