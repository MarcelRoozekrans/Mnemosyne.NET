using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The default <see cref="IReasonletSelector"/>. It drops candidates below the similarity cut-off, then orders the rest
/// by a weighted sum of similarity, smoothed success rate and freshness.
/// </summary>
/// <remarks>
/// A new version's success rate is smoothed toward its parent's smoothed rate when that is above the neutral 0.5, and
/// toward 0.5 otherwise. Inheritance only lifts: a version of a proven procedure starts with its parent's head start, a
/// version of a failing one starts like any new procedure, and, given the same own runs, a version never scores below a
/// Reasonlet without a parent. A run that only the agent judged counts at
/// <see cref="ReasonletRankingOptions.AgentVerdictWeight"/>, in the Reasonlet's own rate and in its parent's, and every
/// match carries the evidence behind its rate. Ties order by similarity descending, then by id ascending, so the order is
/// deterministic.
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
            // Negated so that a NaN similarity, which compares false either way, is dropped too.
            if (!(candidate.Similarity >= cutOff))
            {
                continue;
            }

            var similarity = Clamp(candidate.Similarity);
            var rate = SuccessRate(candidate.Metrics, candidate.ParentMetrics, _options.PriorWeight, _options.AgentVerdictWeight);
            var successRate = Clamp(rate.Rate);
            var freshness = Clamp(Freshness(now - candidate.Metrics.LastExecutedAt, _options.FreshnessHalfLife));
            var total = Clamp(
                (_options.SimilarityWeight * similarity) + (_options.SuccessRateWeight * successRate) + (_options.FreshnessWeight * freshness));
            var evidence = new ReasonletEvidence(candidate.Metrics.Runs, candidate.Metrics.JudgedRuns, rate.UsesParentPrior);
            scored.Add(new ReasonletMatch(candidate.Reasonlet, new ReasonletScore(similarity, successRate, freshness, total), evidence));
        }

        scored.Sort(Compare);
        if (scored.Count > query.Top)
        {
            scored.RemoveRange(query.Top, scored.Count - query.Top);
        }

        return ValueTask.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(scored));
    }

    /// <summary>
    /// Computes the smoothed success rate <c>(weightedSuccesses + k * prior) / (weightedRuns + k)</c>, where a run that only
    /// the agent judged counts at <paramref name="agentVerdictWeight"/> <c>w</c>:
    /// <c>weightedRuns = JudgedRuns + w * (Runs - JudgedRuns)</c> and
    /// <c>weightedSuccesses = JudgedSuccesses + w * (Successes - JudgedSuccesses)</c>. The prior is 0.5 for a root, and
    /// <c>max(parentRate, 0.5)</c> for a version that has a parent, where the parent's rate is its own, weighted the same
    /// way and smoothed with the neutral prior. At <c>w = 1</c> every run counts in full.
    /// </summary>
    /// <returns>The rate, and whether the parent's rate was the prior, which is when it is above 0.5.</returns>
    internal static SmoothedRate SuccessRate(ReasonletMetrics own, ReasonletMetrics? parent, double priorWeight, double agentVerdictWeight)
    {
        var parentRate = parent is { } p ? Smooth(p, NeutralPrior, priorWeight, agentVerdictWeight) : NeutralPrior;
        var usesParentPrior = parentRate > NeutralPrior;
        var prior = usesParentPrior ? parentRate : NeutralPrior;
        return new SmoothedRate(Smooth(own, prior, priorWeight, agentVerdictWeight), usesParentPrior);
    }

    /// <summary>Computes <c>0.5 ^ (age / halfLife)</c>. A negative age, which clock skew can cause, counts as zero.</summary>
    internal static double Freshness(TimeSpan age, TimeSpan halfLife) =>
        age <= TimeSpan.Zero ? 1.0 : Math.Pow(0.5, age / halfLife);

    private static double Smooth(ReasonletMetrics metrics, double prior, double priorWeight, double agentVerdictWeight)
    {
        var weightedRuns = metrics.JudgedRuns + (agentVerdictWeight * (metrics.Runs - metrics.JudgedRuns));
        var weightedSuccesses = metrics.JudgedSuccesses + (agentVerdictWeight * (metrics.Successes - metrics.JudgedSuccesses));
        return (weightedSuccesses + (priorWeight * prior)) / (weightedRuns + priorWeight);
    }

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

    /// <summary>A smoothed success rate and whether its prior came from the parent.</summary>
    /// <param name="Rate">The smoothed success rate.</param>
    /// <param name="UsesParentPrior">Whether the parent's smoothed rate, being above 0.5, was the prior.</param>
    internal readonly record struct SmoothedRate(double Rate, bool UsesParentPrior);
}
