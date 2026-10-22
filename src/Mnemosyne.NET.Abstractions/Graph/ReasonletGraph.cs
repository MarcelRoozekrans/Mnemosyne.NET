using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The graph of nodes and edges that makes up a Reasonlet.</summary>
/// <param name="Nodes">The nodes. Must not be null and no element may be null.</param>
/// <param name="Edges">The edges. Must not be null and no element may be null.</param>
/// <param name="SchemaVersion">The graph schema version. Must be at least 1.</param>
public sealed record ReasonletGraph(
    IReadOnlyList<ReasonletNode> Nodes,
    IReadOnlyList<ReasonletEdge> Edges,
    int SchemaVersion)
{
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
