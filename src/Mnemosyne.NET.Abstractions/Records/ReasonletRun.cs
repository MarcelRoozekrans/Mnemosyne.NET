using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>One run of an existing Reasonlet, to record with <see cref="IReasonletMemory.RecordExecutionAsync"/>.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletRun
{
    /// <summary>Gets the scope the Reasonlet lives in.</summary>
    public required ReasonletScope Scope { get; init; }

    /// <summary>Gets the Reasonlet that was executed. Must not be the default id.</summary>
    public required ReasonletId Id { get; init; }

    /// <summary>Gets the outcome of the run, the agent's own view of it.</summary>
    public required ReasonletOutcome Outcome { get; init; }

    /// <summary>Gets where the run came from. Must not be null.</summary>
    public required ReasonletProvenance Provenance { get; init; }

    /// <summary>
    /// Gets the run's optional question. With <see cref="Answer"/>, it lets a registered evaluator judge the run. It must be
    /// given together with <see cref="Answer"/>; when present it must not be blank or longer than 32,000 code points.
    /// </summary>
    public string? Question { get; init; }

    /// <summary>
    /// Gets the run's optional answer. It reaches the evaluator and nothing else: it is never stored, and never placed in an
    /// error or a telemetry signal. It must be given together with <see cref="Question"/>; when present it must not be blank
    /// or longer than 32,000 code points.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>Validates the run.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => Check.NotDefault(Id, nameof(Id)),
        () => Check.Nested(Outcome.Validate(), nameof(Outcome)),
        () => Check.NotNull(Provenance, nameof(Provenance)),
        () => Check.Nested(Provenance.Validate(), nameof(Provenance)),
        () => OptionalText(Question, nameof(Question)),
        () => OptionalText(Answer, nameof(Answer)),
        () => Question is not null && Answer is null ? Check.Fail(nameof(Answer), "must be set when Question is set") : UnitResult<MnemosyneError>.Success(),
        () => Answer is not null && Question is null ? Check.Fail(nameof(Question), "must be set when Answer is set") : UnitResult<MnemosyneError>.Success());

    /// <summary>Checks a run text that is optional but, when present, must not be blank or too long.</summary>
    internal static UnitResult<MnemosyneError> OptionalText(string? value, string path) =>
        value is null
            ? UnitResult<MnemosyneError>.Success()
            : Check.All(
                () => Check.NotBlank(value, path),
                () => Check.ValidUtf16(value, path),
                () => Check.MaxLength(value, EvaluationLimits.MaxRunTextLength, path));
}
