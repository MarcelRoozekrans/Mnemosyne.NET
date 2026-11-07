using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// An <see cref="IReasonletSelector"/> that returns one version per lineage, and cuts to the query's
/// <see cref="ReasonletQuery.Top"/> after grouping. How it picks the version depends on
/// <see cref="VariantSelectionOptions.Exploration"/>.
/// </summary>
/// <remarks>
/// <para>
/// With <see cref="VariantExploration.None"/>, the default, it asks the inner selector to rank every candidate and keeps
/// the best-ranked version of each lineage. Scoring, the similarity cut-off and the order of ties all come from the inner
/// selector; this selector only drops the lower-ranked versions of a lineage, so the surviving matches keep the inner
/// order. The inner selector receives a query whose <see cref="ReasonletQuery.Top"/> equals the candidate count, so it
/// can exceed the query's limit of 50 when the candidate pool does.
/// </para>
/// <para>
/// With <see cref="VariantExploration.Thompson"/> it picks one version per lineage first and lets the inner selector
/// score and order only the picks. A lineage that has a version with fewer than
/// <see cref="VariantSelectionOptions.MinTrials"/> runs gets the version with the fewest runs. Otherwise each version
/// draws from a Beta posterior whose mean is its smoothed success rate under the ranking options, and the highest draw
/// wins. Ties go to the lowest id. With a <see cref="VariantSelectionOptions.Seed"/> each lineage draws from its own
/// generator, seeded from the seed, the question and the lineage id, and draws for its variants in id order, so the same
/// query on the same data picks the same versions whatever the order of the lineages or of the variants, and whatever the
/// concurrency of the calls. The inner selector receives a query whose <see cref="ReasonletQuery.Top"/> equals the number
/// of picks, so it can exceed the query's limit of 50 when the candidate pool does.
/// </para>
/// </remarks>
public sealed class VariantSelector : IReasonletSelector
{
    private static readonly BetaSampler SharedSampler = new(Random.Shared);

    private readonly IReasonletSelector _inner;
    private readonly VariantSelectionOptions _options;
    private readonly ReasonletRankingOptions _ranking;

    /// <summary>The selector this one wraps.</summary>
    internal IReasonletSelector Inner => _inner;

    /// <summary>Initializes a selector that always keeps the best-ranked version, as <see cref="VariantExploration.None"/> does.</summary>
    /// <param name="inner">The selector that scores and orders the candidates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public VariantSelector(IReasonletSelector inner)
        : this(inner, new VariantSelectionOptions(), new ReasonletRankingOptions())
    {
    }

    /// <summary>Initializes the selector with exploration settings.</summary>
    /// <param name="inner">The selector that scores and orders the candidates.</param>
    /// <param name="options">How a version is picked within a lineage. Must be valid.</param>
    /// <param name="ranking">
    /// The ranking options whose prior weight and agent-verdict weight shape the Beta posteriors. Must be valid when
    /// <see cref="VariantSelectionOptions.Exploration"/> is <see cref="VariantExploration.Thompson"/>, and is not read otherwise.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The options are invalid, or the ranking options are invalid while exploring with Thompson sampling.
    /// </exception>
    public VariantSelector(IReasonletSelector inner, VariantSelectionOptions options, ReasonletRankingOptions ranking)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ranking);
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        if (options.Exploration == VariantExploration.Thompson)
        {
            var rankingValidation = ranking.Validate();
            if (rankingValidation.IsFailure)
            {
                throw new ArgumentException(rankingValidation.Error.Message, nameof(ranking));
            }
        }

        _inner = inner;
        _options = options;
        _ranking = ranking;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is cancelled.</exception>
    public async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        // Inside the async method, so a cancelled token cancels the returned task instead of throwing from the call.
        ct.ThrowIfCancellationRequested();
        return _options.Exploration == VariantExploration.Thompson
            ? await ExploreAsync(query, candidates, ct).ConfigureAwait(false)
            : await BestAsync(query, candidates, ct).ConfigureAwait(false);
    }

    // Only the recall's own activity is tagged, never whatever activity happens to be current.
    private static Activity? RecallActivity()
    {
        var current = Activity.Current;
        return current is { Source.Name: MnemosyneTelemetry.ActivitySourceName } && current.OperationName == RecallTelemetry.RecallActivityName
            ? current
            : null;
    }

    // Whether a variant scoring score beats the best so far: a higher score, or an equal one with a lower id.
    private static bool Beats(double score, ReasonletId id, double bestScore, ReasonletId bestId) =>
        score > bestScore || (score == bestScore && id.Value.CompareTo(bestId.Value) < 0);

    // The index of the highest score, ties by id ascending.
    private static int Best(List<ReasonletCandidate> variants, double[] scores)
    {
        var best = 0;
        for (var i = 1; i < variants.Count; i++)
        {
            if (Beats(scores[i], variants[i].Reasonlet.Id, scores[best], variants[best].Reasonlet.Id))
            {
                best = i;
            }
        }

        return best;
    }

    private async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> BestAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        var ranked = await _inner.RankAsync(query with { Top = Math.Max(1, candidates.Count) }, candidates, ct).ConfigureAwait(false);
        if (ranked.IsFailure)
        {
            return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(ranked.Error);
        }

        var activity = RecallActivity();
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

    private async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> ExploreAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        // Grouped in the order the lineages first appear.
        var lineages = new Dictionary<ReasonletId, List<ReasonletCandidate>>();
        var order = new List<List<ReasonletCandidate>>();
        var largest = 0;
        foreach (var candidate in candidates)
        {
            if (!lineages.TryGetValue(candidate.LineageId, out var variants))
            {
                variants = [];
                lineages.Add(candidate.LineageId, variants);
                order.Add(variants);
            }

            variants.Add(candidate);
            largest = Math.Max(largest, variants.Count);
        }

        var picks = new List<ReasonletCandidate>(order.Count);
        var explored = 0;
        var scores = new double[largest];
        foreach (var variants in order)
        {
            // Store order within a lineage is not stable, so the pick must depend on the set of variants only.
            variants.Sort(static (x, y) => x.Reasonlet.Id.Value.CompareTo(y.Reasonlet.Id.Value));

            // A lineage of one has nothing to choose between, so its pick is never an exploration.
            var pick = 0;
            if (variants.Count > 1)
            {
                pick = Pick(query, variants, scores);
                if (pick != BestRate(variants, scores))
                {
                    explored++;
                }
            }

            picks.Add(variants[pick]);
        }

        var ranked = await _inner.RankAsync(query with { Top = Math.Max(1, picks.Count) }, picks, ct).ConfigureAwait(false);
        if (ranked.IsFailure)
        {
            return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(ranked.Error);
        }

        var matches = ranked.Value.Count > query.Top ? ranked.Value.Take(query.Top).ToList() : ranked.Value;

        // Dropped are the siblings that lost the pick, not the lineages cut by Top or by the inner cut-off.
        var activity = RecallActivity();
        RecallTelemetry.Variants(activity, matches.Count, candidates.Count - picks.Count);
        RecallTelemetry.Explored(activity, explored);

        return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(matches);
    }

    // The index of the variant this lineage serves: forced by MinTrials, else the highest Beta draw.
    // The scores buffer holds at least one slot per variant.
    private int Pick(ReasonletQuery query, List<ReasonletCandidate> variants, double[] scores)
    {
        var forced = false;
        for (var i = 0; i < variants.Count; i++)
        {
            var runs = variants[i].Metrics.Runs;
            forced |= runs < _options.MinTrials;

            // Fewest runs scores highest.
            scores[i] = -runs;
        }

        if (!forced)
        {
            var sampler = _options.Seed is { } seed
                ? new BetaSampler(BetaSampler.SeededRandom(seed, query.Question, variants[0].LineageId))
                : SharedSampler;
            for (var i = 0; i < variants.Count; i++)
            {
                var (alpha, beta) = ReasonletSelector.Posterior(
                    variants[i].Metrics, variants[i].ParentMetrics, _ranking.PriorWeight, _ranking.AgentVerdictWeight);
                scores[i] = sampler.Sample(alpha, beta);
            }
        }

        return Best(variants, scores);
    }

    // The index of the variant with the highest smoothed success rate, ties by id ascending.
    private int BestRate(List<ReasonletCandidate> variants, double[] rates)
    {
        for (var i = 0; i < variants.Count; i++)
        {
            rates[i] = ReasonletSelector.SuccessRate(
                variants[i].Metrics, variants[i].ParentMetrics, _ranking.PriorWeight, _ranking.AgentVerdictWeight).Rate;
        }

        return Best(variants, rates);
    }
}
