using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The ranking port. Implementations order search candidates for a query. Cancellation throws <see cref="OperationCanceledException"/>; it is never returned as a result.</summary>
public interface IReasonletSelector
{
    /// <summary>Ranks candidates for a query.</summary>
    /// <remarks>
    /// The facade validates the query and every candidate before ranking, so the selector may assume valid input.
    /// Error codes: <see cref="MnemosyneErrorCode.Unavailable"/> when the ranking backend fails.
    /// </remarks>
    /// <param name="query">The query being answered.</param>
    /// <param name="candidates">The candidates from the store.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The ranked matches, best first.</returns>
    ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct);
}
