using YamlDotNet.RepresentationModel;

namespace Honua.DevOps.Agent.Operations.DesiredState;

internal sealed record DesiredStateObjectKey(string Kind, string Name, string Namespace);

// A parsed desired-state YAML object plus convenience accessors. Parsing is
// tolerant: structural problems are reported as drift issues by the detector
// rather than thrown, so a single malformed object never aborts the whole scan.
internal sealed record DesiredStateDocument(
    string Path,
    string? ApiVersion,
    string? Kind,
    string? Name,
    string? Namespace,
    YamlMappingNode? Root,
    string? ParseError)
{
    internal bool ParsedOk => ParseError is null && Root is not null;

    internal DesiredStateObjectKey? Key =>
        Kind is not null && Name is not null && Namespace is not null
            ? new DesiredStateObjectKey(Kind, Name, Namespace)
            : null;

    internal static DesiredStateDocument Load(string path, string yamlText)
    {
        try
        {
            using StringReader reader = new(yamlText);
            YamlStream yaml = new();
            yaml.Load(reader);

            if (yaml.Documents.Count != 1)
            {
                return Failed(path, $"Expected exactly one YAML document, found {yaml.Documents.Count}.");
            }

            if (yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                return Failed(path, "Root node is not a mapping.");
            }

            return new DesiredStateDocument(
                Path: path,
                ApiVersion: TryScalar(root, "apiVersion"),
                Kind: TryScalar(root, "kind"),
                Name: TryScalar(root, "metadata", "name"),
                Namespace: TryScalar(root, "metadata", "namespace"),
                Root: root,
                ParseError: null);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            return Failed(path, $"YAML parse error: {exception.Message}");
        }
    }

    private static DesiredStateDocument Failed(string path, string error) =>
        new(path, null, null, null, null, null, error);

    internal string? TryScalar(params string[] pathSegments) => TryScalar(Root, pathSegments);

    internal IReadOnlyList<string>? TrySequence(params string[] pathSegments)
    {
        YamlNode? node = Traverse(Root, pathSegments);
        if (node is not YamlSequenceNode sequence)
        {
            return null;
        }

        return sequence.Children
            .OfType<YamlScalarNode>()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    internal YamlMappingNode? TryMapping(params string[] pathSegments) =>
        Traverse(Root, pathSegments) as YamlMappingNode;

    internal bool HasMapping(params string[] pathSegments) => TryMapping(pathSegments) is not null;

    private static string? TryScalar(YamlNode? root, params string[] pathSegments)
    {
        if (Traverse(root, pathSegments) is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            return scalar.Value;
        }

        return null;
    }

    private static YamlNode? Traverse(YamlNode? root, params string[] pathSegments)
    {
        YamlNode? current = root;
        foreach (string segment in pathSegments)
        {
            if (current is not YamlMappingNode mapping ||
                !mapping.Children.TryGetValue(new YamlScalarNode(segment), out YamlNode? next) ||
                next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
