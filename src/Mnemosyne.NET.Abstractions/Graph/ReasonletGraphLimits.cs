namespace Mnemosyne.NET.Abstractions;

/// <summary>The limits that <see cref="ReasonletGraph.Validate"/> enforces for schema version 1.</summary>
public static class ReasonletGraphLimits
{
    /// <summary>The maximum number of nodes in a graph.</summary>
    public const int MaxNodes = 500;

    /// <summary>The maximum number of edges in a graph.</summary>
    public const int MaxEdges = 2000;

    /// <summary>
    /// The maximum length of a node <c>Id</c>, <c>Type</c> or <c>Tool</c> and an edge <c>From</c> or <c>To</c>, in
    /// Unicode code points, the unit JSON Schema <c>maxLength</c> counts. A surrogate pair counts as one.
    /// </summary>
    public const int MaxIdentifierLength = 256;

    /// <summary>
    /// The maximum length of a node <c>Name</c> or an edge <c>Condition</c>, in Unicode code points, the unit JSON
    /// Schema <c>maxLength</c> counts. A surrogate pair counts as one.
    /// </summary>
    public const int MaxTextLength = 1024;

    /// <summary>
    /// The maximum total size in bytes of all <c>Properties</c> dictionaries in a graph (graph, nodes and edges).
    /// Each dictionary is measured as the UTF-8 length of its canonical JSON object, the form the content hash uses:
    /// compact, with the fixed RFC 8785 escape set, so a supplementary character counts as its 4 raw UTF-8 bytes.
    /// The lengths are summed.
    /// </summary>
    public const int MaxPropertiesBytes = 65536;

    /// <summary>
    /// The maximum nesting depth of a single property value.
    /// A scalar, an empty array or an empty object is level 1, and each enclosing array or object adds one level.
    /// </summary>
    public const int MaxPropertyDepth = 32;
}
