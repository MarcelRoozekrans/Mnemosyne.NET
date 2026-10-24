using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for <see cref="ChatReasonletEvaluator"/>.</summary>
public sealed class ChatEvaluatorOptions
{
    /// <summary>The most code points <see cref="Criteria"/> may hold.</summary>
    internal const int MaxCriteriaLength = 4_000;

    /// <summary>
    /// Gets the name the verdicts carry as <see cref="ReasonletEvaluation.Evaluator"/>. Must not be blank, must be valid
    /// UTF-16 and must not be longer than <see cref="EvaluationLimits.MaxEvaluatorLength"/> code points. Defaults to
    /// <c>llm-judge</c>.
    /// </summary>
    public string EvaluatorName { get; init; } = "llm-judge";

    /// <summary>
    /// Gets extra judging criteria the judge applies on top of its question, such as <c>The answer must cite a source.</c>
    /// They go into the judge's instructions; the reply format stays fixed. When set, they must not be blank, must be valid
    /// UTF-16 and must not be longer than 4,000 code points. Defaults to null, which adds none.
    /// </summary>
    public string? Criteria { get; init; }

    /// <summary>
    /// Gets the service key of a keyed <c>IChatClient</c> for the judge, so a host can give it its own model. Defaults to
    /// null, which uses the unkeyed client.
    /// </summary>
    public object? ChatClientKey { get; init; }

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotBlank(EvaluatorName, nameof(EvaluatorName)),
        () => Check.ValidUtf16(EvaluatorName, nameof(EvaluatorName)),
        () => Check.MaxLength(EvaluatorName, EvaluationLimits.MaxEvaluatorLength, nameof(EvaluatorName)),
        () => Criteria is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Criteria, nameof(Criteria)),
        () => Check.ValidUtf16(Criteria, nameof(Criteria)),
        () => Check.MaxLength(Criteria, MaxCriteriaLength, nameof(Criteria)));
}
