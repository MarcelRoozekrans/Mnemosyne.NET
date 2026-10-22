using System.Text.Json;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A node in a Reasonlet graph.</summary>
/// <remarks>
/// Init-only, so later phases can add members without breaking callers.
/// The well-known <see cref="Type"/> values are <c>tool</c>, <c>retrieve</c>, <c>decision</c>, <c>agent</c> and <c>answer</c>.
/// Other values are allowed.
/// Every string must be valid UTF-16, with no lone surrogate, so it survives UTF-8 storage and hashing unchanged.
/// </remarks>
public sealed record ReasonletNode
{
    /// <summary>
    /// Gets the node identifier. Must not be blank, and at most <see cref="ReasonletGraphLimits.MaxIdentifierLength"/>
    /// code points.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the node type. Must not be blank, and at most <see cref="ReasonletGraphLimits.MaxIdentifierLength"/> code
    /// points.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the optional display name. When present, must not be blank, and at most
    /// <see cref="ReasonletGraphLimits.MaxTextLength"/> code points.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the optional name of the tool this node invokes. When present, must not be blank, and at most
    /// <see cref="ReasonletGraphLimits.MaxIdentifierLength"/> code points.
    /// </summary>
    public string? Tool { get; init; }

    /// <summary>Gets the optional free-form properties of the node.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>Validates the node.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(Id, nameof(Id)),
        () => Check.ValidUtf16(Id, nameof(Id)),
        () => Check.MaxLength(Id, ReasonletGraphLimits.MaxIdentifierLength, nameof(Id)),
        () => Check.NotBlank(Type, nameof(Type)),
        () => Check.ValidUtf16(Type, nameof(Type)),
        () => Check.MaxLength(Type, ReasonletGraphLimits.MaxIdentifierLength, nameof(Type)),
        () => Name is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Name, nameof(Name)),
        () => Check.ValidUtf16(Name, nameof(Name)),
        () => Check.MaxLength(Name, ReasonletGraphLimits.MaxTextLength, nameof(Name)),
        () => Tool is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Tool, nameof(Tool)),
        () => Check.ValidUtf16(Tool, nameof(Tool)),
        () => Check.MaxLength(Tool, ReasonletGraphLimits.MaxIdentifierLength, nameof(Tool)));
}
