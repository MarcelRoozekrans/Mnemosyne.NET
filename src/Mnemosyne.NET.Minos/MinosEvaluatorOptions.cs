using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Minos;

/// <summary>Settings for <see cref="MinosReasonletEvaluator"/>.</summary>
public sealed class MinosEvaluatorOptions
{
    /// <summary>
    /// Gets the probability from which a run counts as a success, in [0, 1]. Defaults to 0.5, the cut-off of Minos's own
    /// <c>Noul.Value</c>.
    /// </summary>
    public double SuccessThreshold { get; init; } = 0.5;

    /// <summary>
    /// Gets the name the verdicts carry as <see cref="ReasonletEvaluation.Evaluator"/>. Must not be blank, must be valid
    /// UTF-16 and must not be longer than <see cref="EvaluationLimits.MaxEvaluatorLength"/> code points. Defaults to
    /// <c>minos</c>.
    /// </summary>
    public string EvaluatorName { get; init; } = "minos";

    /// <summary>
    /// Gets the service key of a keyed <c>IDecisionClient</c> for the judge, so a host can give it its own client. Defaults to
    /// null, which uses the unkeyed client.
    /// </summary>
    public object? DecisionClientKey { get; init; }

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.UnitInterval(SuccessThreshold, nameof(SuccessThreshold)),
        () => Check.NotBlank(EvaluatorName, nameof(EvaluatorName)),
        () => Check.ValidUtf16(EvaluatorName, nameof(EvaluatorName)),
        () => Check.MaxLength(EvaluatorName, EvaluationLimits.MaxEvaluatorLength, nameof(EvaluatorName)));
}
