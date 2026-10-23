using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>
/// The evaluator port. Implementations judge whether one run of a procedure worked, so the success rate behind ranking
/// reflects real results rather than only the agent's own view. Cancellation throws <see cref="OperationCanceledException"/>;
/// it is never returned as a result.
/// </summary>
/// <remarks>
/// The facade calls an evaluator, when one is registered, for each run recorded with both a question and an answer. A
/// human's explicit verdict on feedback overrides the evaluator's, and the agent's own <see cref="ReasonletOutcome.Success"/>
/// counts when no evaluator judged the run.
/// </remarks>
public interface IReasonletEvaluator
{
    /// <summary>Judges one run.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Unavailable"/> when the judge's backend fails. Any other failure, and an
    /// evaluation that fails <see cref="ReasonletEvaluation.Validate"/>, is an adapter bug: the facade throws
    /// <see cref="InvalidOperationException"/> for it. An evaluator must not quote the question or the answer in an error.
    /// </remarks>
    /// <param name="request">The run to judge. It is valid.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The verdict.</returns>
    ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct);
}
