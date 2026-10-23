using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The input to an atomic remember in <see cref="IReasonletStore"/>.</summary>
/// <remarks>
/// <paramref name="Intent"/> and <paramref name="Name"/> are the resolved values. The facade builds the entry with
/// <c>Capture.Intent</c> and <c>Capture.Name</c> set to the same resolved values, so the two never disagree, and
/// <see cref="Validate"/> checks that they are equal whenever the capture's values are non-null. The facade also strips the
/// capture's <see cref="ReasonletCapture.Answer"/>, and <see cref="Validate"/> requires it to be null, so no answer reaches a store.
/// </remarks>
/// <param name="Capture">The capture being stored. Must not be null and must be valid.</param>
/// <param name="Intent">The resolved intent text. Must not be blank.</param>
/// <param name="Name">The resolved Reasonlet name. Must not be blank.</param>
/// <param name="Embedding">The embedding of the capture's question. Must not be empty, and every value must be finite.</param>
/// <param name="Evaluation">
/// The evaluator's verdict on the run the capture records, or null when no evaluator judged it. When present it must be valid.
/// The store derives the run's verdict source and effective success from it.
/// </param>
public sealed record ReasonletStoreEntry(
    ReasonletCapture Capture,
    string Intent,
    string Name,
    ReadOnlyMemory<float> Embedding,
    ReasonletEvaluation? Evaluation = null)
{
    /// <summary>Validates the entry.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Capture, nameof(Capture)),
        () => Check.Nested(Capture.Validate(), nameof(Capture)),
        () => Capture.Answer is null ? UnitResult<MnemosyneError>.Success() : Check.Fail("Capture.Answer", "must be null; an answer is never stored"),
        () => Check.NotBlank(Intent, nameof(Intent)),
        () => Check.MatchesWhenPresent(Intent, Capture.Intent, nameof(Intent), "Capture.Intent"),
        () => Check.NotBlank(Name, nameof(Name)),
        () => Check.MatchesWhenPresent(Name, Capture.Name, nameof(Name), "Capture.Name"),
        () => Check.NotEmpty(Embedding, nameof(Embedding)),
        () => Check.AllFinite(Embedding, nameof(Embedding)),
        () => Evaluation is { } evaluation ? Check.Nested(evaluation.Validate(), nameof(Evaluation)) : UnitResult<MnemosyneError>.Success());
}
