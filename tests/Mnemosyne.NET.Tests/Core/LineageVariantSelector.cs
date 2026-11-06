using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>
/// Test-only proof that a variant strategy fits the selector seam using public members alone:
/// it keeps the best version of each lineage and ranks the lineages by that score.
/// </summary>
internal sealed class LineageVariantSelector(IReasonletSelector inner) : IReasonletSelector
{
    private const int MaxTop = 50;

    public async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        var ranked = await inner.RankAsync(query with { Top = MaxTop }, candidates, ct);
        if (!ranked.IsSuccess)
        {
            return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(ranked.Error);
        }

        IReadOnlyList<ReasonletMatch> variants = ranked.Value
            .GroupBy(match => match.LineageId)
            .Select(group => group.First())
            .OrderByDescending(match => match.Score.Total)
            .Take(query.Top)
            .ToList();
        return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(variants);
    }
}
