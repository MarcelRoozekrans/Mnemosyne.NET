using System.Text.RegularExpressions;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.Samples.Evaluation;

/// <summary>
/// A rule-based evaluator, so the sample judges runs offline with no model. An answer works when it contains a refund
/// reference matching <c>REF-\d+</c> and the word "notified", which may be in any case. It judges the answer alone
/// and ignores the agent's own outcome, so it can overrule an agent that reports success.
/// </summary>
/// <remarks>
/// It never prints the question or the answer and never quotes them in an error: both can hold customer data.
/// </remarks>
internal sealed partial class ChecklistEvaluator : IReasonletEvaluator
{
    /// <summary>The name the facade records as the verdict's evaluator.</summary>
    public const string Id = "sample-checklist";

    /// <inheritdoc />
    public ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var met = RefundReference().IsMatch(request.Answer)
            && request.Answer.Contains("notified", StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(
            Result<ReasonletEvaluation, MnemosyneError>.Success(new ReasonletEvaluation(Success: met, Score: met ? 1.0 : 0.0, Evaluator: Id)));
    }

    [GeneratedRegex(@"REF-\d+", RegexOptions.CultureInvariant)]
    private static partial Regex RefundReference();
}
