using System.Text.Json;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The graph of nodes and edges that makes up a Reasonlet.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletGraph
{
    /// <summary>The schema version written by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the nodes. Must not be null and no element may be null.</summary>
    public required IReadOnlyList<ReasonletNode> Nodes { get; init; }

    /// <summary>Gets the edges. Must not be null and no element may be null.</summary>
    public required IReadOnlyList<ReasonletEdge> Edges { get; init; }

    /// <summary>Gets the graph schema version. Must be at least 1.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the optional free-form properties of the graph.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>Validates the graph.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var header = Check.All(
            () => Check.NotNull(Nodes, nameof(Nodes)),
            () => Check.NotNull(Edges, nameof(Edges)),
            () => Check.Range(SchemaVersion, 1, int.MaxValue, nameof(SchemaVersion)));
        if (header.IsFailure)
        {
            return header;
        }

        for (var i = 0; i < Nodes.Count; i++)
        {
            var path = $"Nodes[{i}]";
            var node = Nodes[i];
            var result = node is null ? Check.NotNull(node, path) : Check.Nested(node.Validate(), path);
            if (result.IsFailure)
            {
                return result;
            }
        }

        for (var i = 0; i < Edges.Count; i++)
        {
            var path = $"Edges[{i}]";
            var edge = Edges[i];
            var result = edge is null ? Check.NotNull(edge, path) : Check.Nested(edge.Validate(), path);
            if (result.IsFailure)
            {
                return result;
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }
}
