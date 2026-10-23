using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The default <see cref="IReasonletSelector"/>. It drops candidates below the similarity cut-off, then orders the rest
/// by a weighted sum of similarity, smoothed success rate and freshness.
/// </summary>
/// <remarks>
/// A new version starts from its parent's smoothed success rate, so a refined Reasonlet is not ranked below an
/// unproven one. Ties order by similarity descending, then by id ascending, so the order is deterministic.
/// </remarks>
public sealed class ReasonletSelector : IReasonletSelector
{
    private const double NeutralPrior = 0.5;

    private readonly ReasonletRankingOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Initializes the selector.</summary>
    /// <param name="options">The ranking options. Must be valid.</param>
    /// <param name="time">The clock that decides what "now" is for freshness.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ReasonletSelector(ReasonletRankingOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        var cutOff = query.MinSimilarity ?? _options.MinSimilarity;
        var now = _time.GetUtcNow();
        var scored = new List<ReasonletMatch>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Similarity < cutOff)
            {
                continue;
            }

            var similarity = Clamp(candidate.Similarity);
            var successRate = Clamp(SuccessRate(candidate.Metrics, candidate.ParentMetrics, _options.PriorWeight));
            var freshness = Clamp(Freshness(now - candidate.Metrics.LastExecutedAt, _options.FreshnessHalfLife));
            var total = Clamp(
                (_options.SimilarityWeight * similarity) + (_options.SuccessRateWeight * successRate) + (_options.FreshnessWeight * freshness));
            scored.Add(new ReasonletMatch(candidate.Reasonlet, new ReasonletScore(similarity, successRate, freshness, total)));
        }

        scored.Sort(Compare);
        if (scored.Count > query.Top)
        {
            scored.RemoveRange(query.Top, scored.Count - query.Top);
        }

        return ValueTask.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(scored));
    }

    /// <summary>
    /// Computes the smoothed success rate <c>(successes + k * prior) / (runs + k)</c>. The prior is 0.5 for a root and the
    /// parent's own rate, smoothed with the neutral prior, for a version that has a parent.
    /// </summary>
    internal static double SuccessRate(ReasonletMetrics own, ReasonletMetrics? parent, double priorWeight)
    {
        var prior = parent is { } p ? Smooth(p, NeutralPrior, priorWeight) : NeutralPrior;
        return Smooth(own, prior, priorWeight);
    }

    /// <summary>Computes <c>0.5 ^ (age / halfLife)</c>. A negative age, which clock skew can cause, counts as zero.</summary>
    internal static double Freshness(TimeSpan age, TimeSpan halfLife) =>
        age <= TimeSpan.Zero ? 1.0 : Math.Pow(0.5, age / halfLife);

    private static double Smooth(ReasonletMetrics metrics, double prior, double priorWeight) =>
        (metrics.Successes + (priorWeight * prior)) / (metrics.Runs + priorWeight);

    private static double Clamp(double value) => Math.Clamp(value, 0.0, 1.0);

    private static int Compare(ReasonletMatch x, ReasonletMatch y)
    {
        var byTotal = y.Score.Total.CompareTo(x.Score.Total);
        if (byTotal != 0)
        {
            return byTotal;
        }

        var bySimilarity = y.Score.Similarity.CompareTo(x.Score.Similarity);
        return bySimilarity != 0 ? bySimilarity : x.Reasonlet.Id.Value.CompareTo(y.Reasonlet.Id.Value);
    }
}
