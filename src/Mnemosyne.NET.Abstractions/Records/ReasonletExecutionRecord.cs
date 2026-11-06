using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>One recorded run in a subject export: the run, its verdict and where the verdict came from.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletExecutionRecord
{
    /// <summary>Gets the id of the run. Must not be the default id.</summary>
    public required ExecutionId Id { get; init; }

    /// <summary>Gets the Reasonlet that was executed. Must not be the default id.</summary>
    public required ReasonletId ReasonletId { get; init; }

    /// <summary>Gets the scope the Reasonlet lives in.</summary>
    public required ReasonletScope Scope { get; init; }

    /// <summary>Gets when the run was recorded.</summary>
    public required DateTimeOffset ExecutedAt { get; init; }

    /// <summary>Gets the outcome of the run, the agent's own view of it.</summary>
    public required ReasonletOutcome Outcome { get; init; }

    /// <summary>Gets the evaluator's verdict on the run, or <see langword="null"/> when none judged it.</summary>
    public ReasonletEvaluation? Evaluation { get; init; }

    /// <summary>Gets the feedback recorded for the run, or <see langword="null"/> when there is none.</summary>
    public ReasonletFeedback? Feedback { get; init; }

    /// <summary>Gets the run's verdict, after the agent's view, any evaluation and any feedback are weighed.</summary>
    public required bool Success { get; init; }

    /// <summary>Gets where <see cref="Success"/> came from. Must be a defined value.</summary>
    public required ReasonletVerdictSource VerdictSource { get; init; }

    /// <summary>Gets where the run came from. Must not be null.</summary>
    public required ReasonletProvenance Provenance { get; init; }

    /// <summary>Validates the record.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotDefault(Id, nameof(Id)),
        () => Check.NotDefault(ReasonletId, nameof(ReasonletId)),
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => Check.Nested(Outcome.Validate(), nameof(Outcome)),
        () => Evaluation is { } evaluation
            ? Check.Nested(evaluation.Validate(), nameof(Evaluation))
            : UnitResult<MnemosyneError>.Success(),
        () => Feedback is { } feedback
            ? Check.Nested(feedback.Validate(), nameof(Feedback))
            : UnitResult<MnemosyneError>.Success(),
        () => Check.Defined(VerdictSource, nameof(VerdictSource)),
        () => Check.NotNull(Provenance, nameof(Provenance)),
        () => Check.Nested(Provenance.Validate(), nameof(Provenance)));
}
