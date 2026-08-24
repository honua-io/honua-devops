using System.Reflection;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Tests;

// Architecture guard for the durable actuation seam (issue #153).
//
// The compile-time grant requirement stops today's writes from bypassing the spine. This
// test stops TOMORROW's: a new BackendGateway method must be classified in the catalog, and
// if it is classified as mutating it must require a grant. A new mutating route added
// without one fails here rather than shipping as a second write authority.
public class BackendMutationCatalogTests
{
    private static readonly HashSet<string> IgnoredMethods = new(StringComparer.Ordinal)
    {
        "GetType", "ToString", "Equals", "GetHashCode"
    };

    // The gateway's callable surface: its internal/public methods. Private transport
    // helpers (PostToHonuaAsync, SendJsonAsync, ...) are the plumbing those routes are built
    // from — they are unreachable from outside the gateway, so a new backend call still has
    // to appear here as an internal method to be usable.
    private static IEnumerable<MethodInfo> GatewayMethods()
        => typeof(BackendGateway)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName
                && !method.IsPrivate
                && !method.Name.StartsWith('<')
                && !IgnoredMethods.Contains(method.Name));

    [Fact]
    public void EveryBackendGatewayMethodIsClassified()
    {
        string[] unclassified =
        [
            .. GatewayMethods()
                .Select(method => method.Name)
                .Distinct(StringComparer.Ordinal)
                .Where(name => !BackendMutationCatalog.Routes.ContainsKey(name))
                .OrderBy(name => name, StringComparer.Ordinal)
        ];

        Assert.True(
            unclassified.Length == 0,
            "New BackendGateway method(s) are not classified in BackendMutationCatalog: "
                + string.Join(", ", unclassified)
                + ". Classify each as non-mutating, or as a BackendMutation routed through ActuationSpine.");
    }

    [Fact]
    public void EveryMutatingRouteRequiresAnActuationGrant()
    {
        List<string> violations = [];

        foreach (MethodInfo method in GatewayMethods())
        {
            if (!BackendMutationCatalog.Routes.TryGetValue(method.Name, out BackendRouteClassification? classification)
                || !classification.Mutates)
            {
                continue;
            }

            bool takesGrant = method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(ActuationSpine.OperationGrant)
                || parameter.ParameterType == typeof(ActuationSpine.MutationGrant));

            if (!takesGrant)
            {
                violations.Add(method.Name);
            }
        }

        Assert.True(
            violations.Count == 0,
            "Mutating BackendGateway route(s) do not require an ActuationSpine grant: "
                + string.Join(", ", violations));
    }

    [Fact]
    public void NoNonMutatingRouteAccidentallyDemandsAGrant()
    {
        // A grant on a read is a signal the classification is wrong in the other direction.
        List<string> violations = [];

        foreach (MethodInfo method in GatewayMethods())
        {
            if (!BackendMutationCatalog.Routes.TryGetValue(method.Name, out BackendRouteClassification? classification)
                || classification.Mutates)
            {
                continue;
            }

            if (method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(ActuationSpine.OperationGrant)
                    || parameter.ParameterType == typeof(ActuationSpine.MutationGrant)))
            {
                violations.Add(method.Name);
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void TheCatalogHasNoEntriesForMethodsThatNoLongerExist()
    {
        HashSet<string> actual = [.. GatewayMethods().Select(method => method.Name)];
        string[] stale =
        [
            .. BackendMutationCatalog.Routes.Keys
                .Where(name => !actual.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
        ];

        Assert.Empty(stale);
    }

    [Fact]
    public void TheMutatingRouteInventoryIsTheExpectedSet()
    {
        // A deliberate, reviewable list. Adding a write authority is a decision, not an
        // accident: changing this set requires changing this assertion.
        string[] expected =
        [
            nameof(BackendMutation.DeployOperationCreate),
            nameof(BackendMutation.DeployOperationRollback),
            nameof(BackendMutation.DeployOperationSubmit),
            nameof(BackendMutation.ManifestApply),
            nameof(BackendMutation.MetadataReleaseCreate),
            nameof(BackendMutation.OpsFindingPropose)
        ];

        string[] actual =
        [
            .. BackendMutationCatalog.MutatingRoutes
                .Select(route => route.Mutation!.Value.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];

        Assert.Equal(expected, actual);
    }
}
