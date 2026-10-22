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

    /// <summary>Gets the graph schema version. Must equal <see cref="CurrentSchemaVersion"/>.</summary>
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

        return Check.All(
            CheckVersion,
            CheckHasNodes,
            CheckUniqueNodeIds,
            CheckEdgeEndpoints,
            CheckConditions,
            CheckDuplicateEdges,
            CheckProperties,
            CheckLimits);
    }

    /// <summary>Computes the content hash: the SHA-256 of the graph's canonical JSON form. Never throws.</summary>
    /// <returns>
    /// 64 lowercase hex characters, or the <see cref="MnemosyneErrorCode.Validation"/> failure from <see cref="Validate"/>.
    /// </returns>
    /// <remarks>
    /// Equal graphs hash the same however their nodes, edges and property members are ordered, and whether their
    /// strings are escaped or not. Arrays inside property values keep their order, numbers keep their raw text so
    /// <c>1</c> and <c>1.0</c> differ, null and empty properties are the same, and <see cref="SchemaVersion"/> is
    /// included. The canonical form is a permanent contract, because stored hashes depend on it.
    /// </remarks>
    public Result<string, MnemosyneError> ComputeContentHash()
    {
        var valid = Validate();
        return valid.IsSuccess
            ? Result<string, MnemosyneError>.Success(CanonicalGraphWriter.Hash(this))
            : Result<string, MnemosyneError>.Failure(valid.Error);
    }

    private UnitResult<MnemosyneError> CheckVersion() =>
        SchemaVersion == CurrentSchemaVersion
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(nameof(SchemaVersion), $"unsupported schema version {SchemaVersion}; supported version is {CurrentSchemaVersion}");

    private UnitResult<MnemosyneError> CheckHasNodes() =>
        Nodes.Count == 0 ? Check.Fail(nameof(Nodes), "must contain at least one node") : UnitResult<MnemosyneError>.Success();

    private UnitResult<MnemosyneError> CheckUniqueNodeIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Nodes.Count; i++)
        {
            if (!seen.Add(Nodes[i].Id))
            {
                return Check.Fail($"Nodes[{i}].Id", $"duplicate node id \"{Nodes[i].Id}\"");
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private UnitResult<MnemosyneError> CheckEdgeEndpoints()
    {
        var ids = new HashSet<string>(Nodes.Select(n => n.Id), StringComparer.Ordinal);
        for (var i = 0; i < Edges.Count; i++)
        {
            if (!ids.Contains(Edges[i].From))
            {
                return Check.Fail($"Edges[{i}].From", $"refers to unknown node \"{Edges[i].From}\"");
            }

            if (!ids.Contains(Edges[i].To))
            {
                return Check.Fail($"Edges[{i}].To", $"refers to unknown node \"{Edges[i].To}\"");
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private UnitResult<MnemosyneError> CheckConditions()
    {
        for (var i = 0; i < Edges.Count; i++)
        {
            if (Edges[i].Condition is { } condition && string.IsNullOrWhiteSpace(condition))
            {
                return Check.Fail($"Edges[{i}].Condition", "must not be blank when present");
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private UnitResult<MnemosyneError> CheckDuplicateEdges()
    {
        var seen = new HashSet<(string, string, string?)>();
        for (var i = 0; i < Edges.Count; i++)
        {
            if (!seen.Add((Edges[i].From, Edges[i].To, Edges[i].Condition)))
            {
                return Check.Fail($"Edges[{i}]", "duplicate edge with the same From, To and Condition");
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private UnitResult<MnemosyneError> CheckProperties()
    {
        var graph = PropertyChecks.Validate(Properties, nameof(Properties));
        if (graph.IsFailure)
        {
            return graph;
        }

        for (var i = 0; i < Nodes.Count; i++)
        {
            var result = PropertyChecks.Validate(Nodes[i].Properties, $"Nodes[{i}].Properties");
            if (result.IsFailure)
            {
                return result;
            }
        }

        for (var i = 0; i < Edges.Count; i++)
        {
            var result = PropertyChecks.Validate(Edges[i].Properties, $"Edges[{i}].Properties");
            if (result.IsFailure)
            {
                return result;
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private UnitResult<MnemosyneError> CheckLimits()
    {
        if (Nodes.Count > ReasonletGraphLimits.MaxNodes)
        {
            return Check.Fail(nameof(Nodes), $"must not contain more than {ReasonletGraphLimits.MaxNodes} nodes");
        }

        if (Edges.Count > ReasonletGraphLimits.MaxEdges)
        {
            return Check.Fail(nameof(Edges), $"must not contain more than {ReasonletGraphLimits.MaxEdges} edges");
        }

        long total = PropertyChecks.SerializedSize(Properties);
        foreach (var node in Nodes)
        {
            total += PropertyChecks.SerializedSize(node.Properties);
        }

        foreach (var edge in Edges)
        {
            total += PropertyChecks.SerializedSize(edge.Properties);
        }

        return total > ReasonletGraphLimits.MaxPropertiesBytes
            ? Check.Fail(nameof(Properties), $"total serialized size of all properties must not exceed {ReasonletGraphLimits.MaxPropertiesBytes} bytes")
            : UnitResult<MnemosyneError>.Success();
    }
}
