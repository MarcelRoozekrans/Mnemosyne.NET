using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A node in a Reasonlet graph.</summary>
/// <param name="Id">The node identifier. Must not be blank.</param>
/// <param name="Type">The node type. Must not be blank.</param>
public sealed record ReasonletNode(string Id, string Type)
{
    /// <summary>Validates the node.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(Id, nameof(Id)),
        () => Check.NotBlank(Type, nameof(Type)));
}
