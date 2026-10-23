using System.Buffers;
using System.Text;
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
    /// Gets the name the verdicts carry as <see cref="ReasonletEvaluation.Evaluator"/>. Must not be blank and must not be
    /// longer than 100 code points. Defaults to <c>minos</c>.
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
        () => string.IsNullOrWhiteSpace(EvaluatorName)
            ? Check.Fail(nameof(EvaluatorName), "must not be blank")
            : UnitResult<MnemosyneError>.Success(),
        () => CodePoints(EvaluatorName) switch
        {
            < 0 => Check.Fail(nameof(EvaluatorName), "must be valid UTF-16"),
            > EvaluationLimits.MaxEvaluatorLength => Check.Fail(nameof(EvaluatorName), $"must not be longer than {EvaluationLimits.MaxEvaluatorLength} code points"),
            _ => UnitResult<MnemosyneError>.Success(),
        });

    /// <summary>Counts the code points in a string, a surrogate pair once, or returns -1 when it holds a lone surrogate.</summary>
    private static int CodePoints(string value)
    {
        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
            {
                return -1;
            }

            remaining = remaining[consumed..];
            count++;
        }

        return count;
    }
}
