using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A directed edge in a Reasonlet graph.</summary>
/// <param name="From">The source node identifier. Must not be blank.</param>
/// <param name="To">The target node identifier. Must not be blank.</param>
public sealed record ReasonletEdge(string From, string To)
{
    /// <summary>Validates the edge.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(From, nameof(From)),
        () => Check.NotBlank(To, nameof(To)));
}
