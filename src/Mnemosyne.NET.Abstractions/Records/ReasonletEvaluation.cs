using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>An evaluator's verdict on one run.</summary>
/// <remarks>An evaluator's reasoning is not part of the verdict and is never stored, since it could quote the answer.</remarks>
/// <param name="Success">Whether the run worked.</param>
/// <param name="Score">How well the run worked, from 0 to 1.</param>
/// <param name="Evaluator">
/// A short identifier of the judge, such as <c>llm-judge</c> or <c>minos</c>. Must not be blank and must not be longer than
/// 100 code points. It is stored and appears in telemetry, so it names the judge and nothing about the run.
/// </param>
public readonly record struct ReasonletEvaluation(bool Success, double Score, string Evaluator)
{
    /// <summary>Validates the evaluation. The default value is invalid, because it names no evaluator.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var score = Score;
        var evaluator = Evaluator;
        return Check.All(
            () => Check.UnitInterval(score, nameof(Score)),
            () => Check.NotBlank(evaluator, nameof(Evaluator)),
            () => Check.ValidUtf16(evaluator, nameof(Evaluator)),
            () => Check.MaxLength(evaluator, EvaluationLimits.MaxEvaluatorLength, nameof(Evaluator)));
    }
}
