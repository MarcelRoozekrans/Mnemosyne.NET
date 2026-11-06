using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The input to an atomic remember in <see cref="IReasonletStore"/>.</summary>
/// <remarks>
/// <see cref="Intent"/> and <see cref="Name"/> are the resolved values. The facade builds the entry with
/// <c>Capture.Intent</c> and <c>Capture.Name</c> set to the same resolved values, so the two never disagree, and
/// <see cref="Validate"/> checks that they are equal whenever the capture's values are non-null. The facade also strips the
/// capture's <see cref="ReasonletCapture.Answer"/>, and <see cref="Validate"/> requires it to be null, so no answer reaches a store.
/// Init-only, so later phases can add members without breaking callers.
/// </remarks>
public sealed record ReasonletStoreEntry
{
    /// <summary>Gets the capture being stored. Must not be null and must be valid.</summary>
    public required ReasonletCapture Capture { get; init; }

    /// <summary>Gets the resolved intent text. Must not be blank.</summary>
    public required string Intent { get; init; }

    /// <summary>Gets the resolved Reasonlet name. Must not be blank.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the embedding of the capture's question. Must not be empty, and every value must be finite.</summary>
    public required ReadOnlyMemory<float> Embedding { get; init; }

    /// <summary>
    /// Gets the evaluator's verdict on the run the capture records, or null when no evaluator judged it. When present it must be valid.
    /// The store derives the run's verdict source and effective success from it. Defaults to null.
    /// </summary>
    public ReasonletEvaluation? Evaluation { get; init; }

    /// <summary>
    /// Gets a value indicating whether the review gate is on: a new Reasonlet or a new version is stored as
    /// <see cref="ReasonletStatus.PendingReview"/> instead of <see cref="ReasonletStatus.Active"/>. An execution-only capture never
    /// changes its parent's status. Defaults to false.
    /// </summary>
    public bool ReviewRequired { get; init; }

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
