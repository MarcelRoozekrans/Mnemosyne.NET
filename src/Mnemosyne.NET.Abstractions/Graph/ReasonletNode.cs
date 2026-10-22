using System.Text.Json;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A node in a Reasonlet graph.</summary>
/// <remarks>
/// Init-only, so later phases can add members without breaking callers.
/// The well-known <see cref="Type"/> values are <c>tool</c>, <c>retrieve</c>, <c>decision</c>, <c>agent</c> and <c>answer</c>.
/// Other values are allowed.
/// </remarks>
public sealed record ReasonletNode
{
    /// <summary>Gets the node identifier. Must not be blank.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the node type. Must not be blank.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the optional display name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the optional name of the tool this node invokes.</summary>
    public string? Tool { get; init; }

    /// <summary>Gets the optional free-form properties of the node.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>Validates the node.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(Id, nameof(Id)),
        () => Check.NotBlank(Type, nameof(Type)));
}
