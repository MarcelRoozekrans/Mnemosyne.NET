using System.Buffers;
using System.Text;
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
    /// Gets the name the verdicts carry as <see cref="ReasonletEvaluation.Evaluator"/>. Must not be blank and must not be
    /// longer than 100 code points. Defaults to <c>llm-judge</c>.
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
        () => string.IsNullOrWhiteSpace(EvaluatorName)
            ? Check.Fail(nameof(EvaluatorName), "must not be blank")
            : UnitResult<MnemosyneError>.Success(),
        () => CheckLength(EvaluatorName, EvaluationLimits.MaxEvaluatorLength, nameof(EvaluatorName)),
        () => Criteria is not null && string.IsNullOrWhiteSpace(Criteria)
            ? Check.Fail(nameof(Criteria), "must not be blank")
            : UnitResult<MnemosyneError>.Success(),
        () => Criteria is null
            ? UnitResult<MnemosyneError>.Success()
            : CheckLength(Criteria, MaxCriteriaLength, nameof(Criteria)));

    /// <summary>Fails when a string holds a lone surrogate or more than <paramref name="max"/> code points.</summary>
    private static UnitResult<MnemosyneError> CheckLength(string value, int max, string path) => CodePoints(value) switch
    {
        < 0 => Check.Fail(path, "must be valid UTF-16"),
        var count when count > max => Check.Fail(path, $"must not be longer than {max} code points"),
        _ => UnitResult<MnemosyneError>.Success(),
    };

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
