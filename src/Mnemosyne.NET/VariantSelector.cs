using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// An <see cref="IReasonletSelector"/> that returns one version per lineage. It asks the inner selector to rank every
/// candidate, keeps the best-ranked version of each lineage, and cuts to the query's <see cref="ReasonletQuery.Top"/>
/// after grouping.
/// </summary>
/// <remarks>
/// Scoring, the similarity cut-off and the order of ties all come from the inner selector. This selector only drops the
/// lower-ranked versions of a lineage, so the surviving matches keep the inner order.
/// </remarks>
public sealed class VariantSelector : IReasonletSelector
{
    private readonly IReasonletSelector _inner;

    /// <summary>The selector this one wraps.</summary>
    internal IReasonletSelector Inner => _inner;

    /// <summary>Initializes the selector.</summary>
    /// <param name="inner">The selector that scores and orders the candidates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public VariantSelector(IReasonletSelector inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is cancelled.</exception>
    public async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ranked = await _inner.RankAsync(query with { Top = Math.Max(1, candidates.Count) }, candidates, ct).ConfigureAwait(false);
        if (ranked.IsFailure)
        {
            return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(ranked.Error);
        }

        // Only the recall's own activity is tagged, never whatever activity happens to be current.
        var current = Activity.Current;
        var activity = current is { Source.Name: MnemosyneTelemetry.ActivitySourceName } && current.OperationName == RecallTelemetry.RecallActivityName
            ? current
            : null;

        var seen = new HashSet<ReasonletId>();
        var variants = new List<ReasonletMatch>();
        foreach (var match in ranked.Value)
        {
            // Past Top nothing more is returned. Only a tagged recall keeps walking, to count the lineages it saw.
            if (variants.Count >= query.Top && activity is null)
            {
                break;
            }

            if (seen.Add(match.LineageId) && variants.Count < query.Top)
            {
                variants.Add(match);
            }
        }

        // Dropped are the versions left out because a better sibling won, not the lineages cut by Top.
        RecallTelemetry.Variants(activity, variants.Count, ranked.Value.Count - seen.Count);

        return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(variants);
    }
}
