using System.Text.Json;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A directed edge in a Reasonlet graph.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletEdge
{
    /// <summary>Gets the source node identifier. Must not be blank.</summary>
    public required string From { get; init; }

    /// <summary>Gets the target node identifier. Must not be blank.</summary>
    public required string To { get; init; }

    /// <summary>Gets the optional condition under which this edge is followed.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets the optional free-form properties of the edge.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>Validates the edge.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(From, nameof(From)),
        () => Check.NotBlank(To, nameof(To)));
}
