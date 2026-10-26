using System.Text.Json;
using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Tests;

internal static class ProviderDisclosureTestPolicy
{
    internal static ProviderDisclosureSnapshot Allow(string provider, string model, string destination,
        string useCase = "devops-operator", string classification = "unclassified", string kind = "openai") => new(JsonSerializer.Serialize(new
        {
            workloadDataClassification = classification,
            approvals = new[] { new { provider, kind, destination, model, useCase, dataClassification = classification } }
        }));
}
