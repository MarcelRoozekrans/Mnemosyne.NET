using System.Text.Json;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A directed edge in a Reasonlet graph.</summary>
/// <remarks>
/// Init-only, so later phases can add members without breaking callers.
/// Every string must be valid UTF-16, with no lone surrogate, so it survives UTF-8 storage and hashing unchanged.
/// </remarks>
public sealed record ReasonletEdge
{
    /// <summary>
    /// Gets the source node identifier. Must not be blank, and at most
    /// <see cref="ReasonletGraphLimits.MaxIdentifierLength"/> code points.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// Gets the target node identifier. Must not be blank, and at most
    /// <see cref="ReasonletGraphLimits.MaxIdentifierLength"/> code points.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// Gets the optional condition under which this edge is followed; null means unconditional. When present, must
    /// not be blank, and at most <see cref="ReasonletGraphLimits.MaxTextLength"/> code points.
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>Gets the optional free-form properties of the edge.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    /// <summary>Validates the edge.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(From, nameof(From)),
        () => Check.ValidUtf16(From, nameof(From)),
        () => Check.MaxLength(From, ReasonletGraphLimits.MaxIdentifierLength, nameof(From)),
        () => Check.NotBlank(To, nameof(To)),
        () => Check.ValidUtf16(To, nameof(To)),
        () => Check.MaxLength(To, ReasonletGraphLimits.MaxIdentifierLength, nameof(To)),
        () => Condition is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Condition, nameof(Condition)),
        () => Check.ValidUtf16(Condition, nameof(Condition)),
        () => Check.MaxLength(Condition, ReasonletGraphLimits.MaxTextLength, nameof(Condition)));
}
