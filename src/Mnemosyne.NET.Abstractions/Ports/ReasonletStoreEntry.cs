using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The input to an atomic remember in <see cref="IReasonletStore"/>.</summary>
/// <remarks>
/// <paramref name="Intent"/> and <paramref name="Name"/> are the resolved values. The facade builds the entry with
/// <c>Capture.Intent</c> and <c>Capture.Name</c> set to the same resolved values, so the two never disagree, and
/// <see cref="Validate"/> checks that they are equal whenever the capture's values are non-null.
/// </remarks>
/// <param name="Capture">The capture being stored. Must not be null and must be valid.</param>
/// <param name="Intent">The resolved intent text. Must not be blank.</param>
/// <param name="Name">The resolved Reasonlet name. Must not be blank.</param>
/// <param name="Embedding">The embedding of the capture's question. Must not be empty, and every value must be finite.</param>
public sealed record ReasonletStoreEntry(
    ReasonletCapture Capture,
    string Intent,
    string Name,
    ReadOnlyMemory<float> Embedding)
{
    /// <summary>Validates the entry.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Capture, nameof(Capture)),
        () => Check.Nested(Capture.Validate(), nameof(Capture)),
        () => Check.NotBlank(Intent, nameof(Intent)),
        () => Check.MatchesWhenPresent(Intent, Capture.Intent, nameof(Intent), "Capture.Intent"),
        () => Check.NotBlank(Name, nameof(Name)),
        () => Check.MatchesWhenPresent(Name, Capture.Name, nameof(Name), "Capture.Name"),
        () => Check.NotEmpty(Embedding, nameof(Embedding)),
        () => Check.AllFinite(Embedding, nameof(Embedding)));
}
