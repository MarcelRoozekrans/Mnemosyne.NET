using System.Text.Json;
using Mnemosyne.NET.Abstractions.Validation;
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
    /// <remarks>
    /// The checks run in this order, and the first failure wins:
    /// <list type="number">
    /// <item><description><see cref="Nodes"/> and <see cref="Edges"/> are not null.</description></item>
    /// <item><description><see cref="SchemaVersion"/> is a supported version.</description></item>
    /// <item><description>There is at least one node.</description></item>
    /// <item><description>
    /// The node and edge counts are within <see cref="ReasonletGraphLimits.MaxNodes"/> and
    /// <see cref="ReasonletGraphLimits.MaxEdges"/>, so an oversized graph is rejected before any per-element work.
    /// </description></item>
    /// <item><description>
    /// Each node, then each edge, is not null and passes its own <c>Validate</c>: required strings not blank, optional
    /// <c>Name</c>, <c>Tool</c> and <c>Condition</c> not blank when present, every string valid UTF-16 and within its
    /// length limit.
    /// </description></item>
    /// <item><description>Node ids are unique.</description></item>
    /// <item><description>Every edge's <c>From</c> and <c>To</c> name an existing node.</description></item>
    /// <item><description>No two edges share <c>From</c>, <c>To</c> and <c>Condition</c>.</description></item>
    /// <item><description>Every <c>Properties</c> dictionary has valid, unique keys and valid values.</description></item>
    /// <item><description>
    /// The canonical size of all <c>Properties</c> together is within <see cref="ReasonletGraphLimits.MaxPropertiesBytes"/>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Nodes, nameof(Nodes)),
        () => Check.NotNull(Edges, nameof(Edges)),
        CheckVersion,
        CheckHasNodes,
        CheckCounts,
        CheckElements,
        CheckUniqueNodeIds,
        CheckEdgeEndpoints,
        CheckDuplicateEdges,
        CheckProperties,
        CheckPropertiesSize);

    /// <summary>Computes the content hash: the SHA-256 of the graph's canonical JSON form. Never throws.</summary>
    /// <returns>
    /// 64 lowercase hex characters, or the <see cref="MnemosyneErrorCode.Validation"/> failure from <see cref="Validate"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Equal graphs hash the same however their nodes, edges and property members are ordered, and whether their
    /// strings are escaped or not. Arrays inside property values keep their order, numbers keep their raw text so
    /// <c>1</c> and <c>1.0</c> differ, null and empty properties are the same, and <see cref="SchemaVersion"/> is
    /// included. The canonical form is a permanent contract, because stored hashes depend on it.
    /// </para>
    /// <para>
    /// Strings are written with the fixed RFC 8785 (JCS) escape set: only <c>"</c>, <c>\</c> and U+0000 to U+001F are
    /// escaped, and everything else is raw UTF-8. This differs on purpose from <see cref="ReasonletGraphJson"/>, whose
    /// encoder follows the runtime's Unicode tables, so a .NET upgrade cannot change a hash.
    /// </para>
    /// <para>
    /// Members, nodes and edges are sorted in .NET ordinal order, which compares UTF-16 code units, not UTF-8 bytes or
    /// code points. That is RFC 8785's own UTF-16 code-unit order, so a JCS implementation reproduces the canonical
    /// form, apart from raw number text, which JCS would normalize. Hashes are computed in .NET, by this method.
    /// </para>
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

    private UnitResult<MnemosyneError> CheckCounts()
    {
        if (Nodes.Count > ReasonletGraphLimits.MaxNodes)
        {
            return Check.Fail(nameof(Nodes), $"must not contain more than {ReasonletGraphLimits.MaxNodes} nodes");
        }

        return Edges.Count > ReasonletGraphLimits.MaxEdges
            ? Check.Fail(nameof(Edges), $"must not contain more than {ReasonletGraphLimits.MaxEdges} edges")
            : UnitResult<MnemosyneError>.Success();
    }

    private UnitResult<MnemosyneError> CheckElements()
    {
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

    private UnitResult<MnemosyneError> CheckPropertiesSize()
    {
        long total = CanonicalGraphWriter.PropertiesSize(Properties);
        foreach (var node in Nodes)
        {
            total += CanonicalGraphWriter.PropertiesSize(node.Properties);
        }

        foreach (var edge in Edges)
        {
            total += CanonicalGraphWriter.PropertiesSize(edge.Properties);
        }

        return total > ReasonletGraphLimits.MaxPropertiesBytes
            ? Check.Fail(nameof(Properties), $"total canonical size of all properties must not exceed {ReasonletGraphLimits.MaxPropertiesBytes} bytes")
            : UnitResult<MnemosyneError>.Success();
    }
}
