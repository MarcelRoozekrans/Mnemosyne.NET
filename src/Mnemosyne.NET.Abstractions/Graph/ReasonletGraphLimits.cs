namespace Mnemosyne.NET.Abstractions;

/// <summary>The limits that <see cref="ReasonletGraph.Validate"/> enforces for schema version 1.</summary>
public static class ReasonletGraphLimits
{
    /// <summary>The maximum number of nodes in a graph.</summary>
    public const int MaxNodes = 500;

    /// <summary>The maximum number of edges in a graph.</summary>
    public const int MaxEdges = 2000;

    /// <summary>
    /// The maximum total size in bytes of all <c>Properties</c> dictionaries in a graph (graph, nodes and edges).
    /// Each dictionary is measured as the UTF-8 length of its compact JSON serialization, and the lengths are summed.
    /// </summary>
    public const int MaxPropertiesBytes = 65536;

    /// <summary>
    /// The maximum nesting depth of a single property value.
    /// A scalar, an empty array or an empty object is level 1, and each enclosing array or object adds one level.
    /// </summary>
    public const int MaxPropertyDepth = 32;
}
