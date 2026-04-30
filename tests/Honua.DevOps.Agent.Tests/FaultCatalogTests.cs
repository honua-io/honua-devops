using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Tests;

public class FaultCatalogTests
{
    [Fact]
    public void All_ContainsAtLeast100Scenarios()
    {
        IReadOnlyList<FaultScenario> scenarios = FaultCatalog.All;
        Assert.True(scenarios.Count >= 100, $"Expected at least 100 scenarios, got {scenarios.Count}.");
    }

    [Fact]
    public void All_ScenariosHaveUniqueIds()
    {
        IReadOnlyList<FaultScenario> scenarios = FaultCatalog.All;
        string[] ids = scenarios.Select(s => s.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_ScenariosHaveUniqueNames()
    {
        IReadOnlyList<FaultScenario> scenarios = FaultCatalog.All;
        string[] names = scenarios.Select(s => s.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_ScenariosHaveRequiredFields()
    {
        foreach (FaultScenario scenario in FaultCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Id), $"Scenario missing Id.");
            Assert.False(string.IsNullOrWhiteSpace(scenario.Name), $"Scenario {scenario.Id} missing Name.");
            Assert.False(string.IsNullOrWhiteSpace(scenario.TargetCloud), $"Scenario {scenario.Id} missing TargetCloud.");
            Assert.False(string.IsNullOrWhiteSpace(scenario.TargetRuntime), $"Scenario {scenario.Id} missing TargetRuntime.");
            Assert.False(string.IsNullOrWhiteSpace(scenario.InjectionMethod), $"Scenario {scenario.Id} missing InjectionMethod.");
            Assert.False(string.IsNullOrWhiteSpace(scenario.RollbackPath), $"Scenario {scenario.Id} missing RollbackPath.");
            Assert.False(string.IsNullOrWhiteSpace(scenario.CleanupPath), $"Scenario {scenario.Id} missing CleanupPath.");
            Assert.NotEmpty(scenario.ExpectedSymptoms);
            Assert.NotEmpty(scenario.SafeRemediationOptions);
        }
    }

    [Fact]
    public void All_CoversBothAwsAndAzure()
    {
        IReadOnlyList<FaultScenario> awsScenarios = FaultCatalog.ByCloud("aws");
        IReadOnlyList<FaultScenario> azureScenarios = FaultCatalog.ByCloud("azure");

        Assert.True(awsScenarios.Count >= 50, $"Expected at least 50 AWS scenarios, got {awsScenarios.Count}.");
        Assert.True(azureScenarios.Count >= 50, $"Expected at least 50 Azure scenarios, got {azureScenarios.Count}.");
    }

    [Fact]
    public void All_CoversAtLeast25FaultCategories()
    {
        FaultCategory[] categories = FaultCatalog.All
            .Select(s => s.Category)
            .Distinct()
            .ToArray();

        Assert.True(categories.Length >= 25, $"Expected at least 25 fault categories, got {categories.Length}.");
    }

    [Fact]
    public void All_CoversAllRemediationScopes()
    {
        RemediationScope[] scopes = FaultCatalog.All
            .Select(s => s.RemediationScope)
            .Distinct()
            .ToArray();

        Assert.Contains(RemediationScope.AdvisoryOnly, scopes);
        Assert.Contains(RemediationScope.ReadOnlyDiagnosis, scopes);
        Assert.Contains(RemediationScope.WriteCapable, scopes);
    }

    [Fact]
    public void Resolve_FindsExistingScenario()
    {
        FaultScenario? scenario = FaultCatalog.Resolve("FAULT-001");
        Assert.NotNull(scenario);
        Assert.Equal("Invalid Postgres password secret", scenario!.Name);
        Assert.Equal(FaultCategory.SecretCredential, scenario.Category);
    }

    [Fact]
    public void Resolve_ReturnsNullForUnknownScenario()
    {
        FaultScenario? scenario = FaultCatalog.Resolve("FAULT-999");
        Assert.Null(scenario);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        FaultScenario? scenario = FaultCatalog.Resolve("fault-001");
        Assert.NotNull(scenario);
    }

    [Fact]
    public void ByCategory_ReturnsCorrectScenarios()
    {
        IReadOnlyList<FaultScenario> scenarios = FaultCatalog.ByCategory(FaultCategory.PostgresPerformance);
        Assert.True(scenarios.Count >= 2);
        Assert.All(scenarios, s => Assert.Equal(FaultCategory.PostgresPerformance, s.Category));
    }

    [Fact]
    public void ByCloud_FiltersByCloudProvider()
    {
        IReadOnlyList<FaultScenario> awsOnly = FaultCatalog.ByCloud("aws");
        Assert.All(awsOnly, s => Assert.Contains("aws", s.TargetCloud, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("FAULT-001", "secret-credential")]
    [InlineData("FAULT-005", "ingress-gateway")]
    [InlineData("FAULT-010", "rollout-readiness")]
    [InlineData("FAULT-013", "postgres-performance")]
    [InlineData("FAULT-016", "gitops-drift")]
    public void FaultCategoryExtensions_ToConfigValue_MatchesExpected(string scenarioId, string expectedCategory)
    {
        FaultScenario? scenario = FaultCatalog.Resolve(scenarioId);
        Assert.NotNull(scenario);
        Assert.Equal(expectedCategory, scenario!.Category.ToConfigValue());
    }

    [Fact]
    public void All_ScenariosHaveLogOrMetricEvidence()
    {
        foreach (FaultScenario scenario in FaultCatalog.All)
        {
            bool hasEvidence = scenario.ExpectedLogEvidence.Count > 0 ||
                               scenario.ExpectedMetricEvidence.Count > 0;
            Assert.True(hasEvidence, $"Scenario {scenario.Id} ({scenario.Name}) has no log or metric evidence.");
        }
    }
}
