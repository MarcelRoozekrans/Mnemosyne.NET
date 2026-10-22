using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A request for the best matching Reasonlets.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletQuery
{
    /// <summary>Gets the scope to search.</summary>
    public required ReasonletScope Scope { get; init; }

    /// <summary>Gets the question to match. Must not be blank.</summary>
    public required string Question { get; init; }

    /// <summary>
    /// Gets the run and subject this recall serves, for audit and erasure by subject. Must not be null.
    /// </summary>
    public required ReasonletProvenance Provenance { get; init; }

    /// <summary>Gets an optional intent filter. When present it must not be blank.</summary>
    public string? Intent { get; init; }

    /// <summary>Gets the maximum number of matches, from 1 to 50. Defaults to 3.</summary>
    public int Top { get; init; } = 3;

    /// <summary>Validates the query.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => Check.NotBlank(Question, nameof(Question)),
        () => Check.NotNull(Provenance, nameof(Provenance)),
        () => Check.Nested(Provenance.Validate(), nameof(Provenance)),
        () => Intent is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Intent, nameof(Intent)),
        () => Check.Range(Top, 1, 50, nameof(Top)));
}
