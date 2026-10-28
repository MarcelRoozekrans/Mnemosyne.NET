using Xunit;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;

namespace Mnemosyne.NET.Tests.Core;

public sealed class ReasonletSelectorTests
{
    private const double Tolerance = 1e-12;
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ReasonletSelector Selector(ReasonletRankingOptions? options = null) =>
        new(options ?? new ReasonletRankingOptions(), new FixedTimeProvider(Now));

    private static ReasonletMetrics Metrics(int runs, int successes, DateTimeOffset? executed = null) =>
        new(runs, successes, null, executed ?? Now);

    private static ReasonletMetrics Judged(int runs, int successes, int judgedRuns, int judgedSuccesses) =>
        new(runs, successes, null, Now, judgedRuns, judgedSuccesses);

    // The phase 1.6 formula, before agent verdicts were weighted: every run counts in full.
    private static double UnweightedRate(ReasonletMetrics own, ReasonletMetrics? parent, double k)
    {
        var prior = parent is { } p ? Math.Max((p.Successes + (k * 0.5)) / (p.Runs + k), 0.5) : 0.5;
        return (own.Successes + (k * prior)) / (own.Runs + k);
    }

    private static ReasonletMetrics RandomMetrics(Random random)
    {
        var runs = random.Next(0, 1000);
        var successes = random.Next(0, runs + 1);
        var judgedRuns = random.Next(0, runs + 1);
        var minJudgedSuccesses = Math.Max(0, successes - (runs - judgedRuns));
        var maxJudgedSuccesses = Math.Min(judgedRuns, successes);
        var metrics = Judged(runs, successes, judgedRuns, random.Next(minJudgedSuccesses, maxJudgedSuccesses + 1));
        Assert.True(metrics.Validate().IsSuccess);
        return metrics;
    }

    private static readonly ReasonletRankingOptions FullAgentWeight = new() { AgentVerdictWeight = 1 };

    private static ReasonletCandidate Candidate(double similarity, ReasonletMetrics? metrics = null, ReasonletMetrics? parent = null, ReasonletId? id = null) =>
        new(TestData.Definition() with { Id = id ?? ReasonletId.New() }, similarity, metrics ?? Metrics(0, 0), parent);

    private static async Task<IReadOnlyList<ReasonletMatch>> Rank(ReasonletSelector selector, ReasonletQuery query, params ReasonletCandidate[] candidates)
    {
        var result = await selector.RankAsync(query, candidates, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    [Fact]
    public void SuccessRateOfAnUntriedRootIsNeutral() =>
        Assert.Equal(0.5, ReasonletSelector.SuccessRate(Metrics(0, 0), null, 5, 1).Rate, Tolerance);

    [Fact]
    public void SuccessRateIsSmoothedTowardsTheNeutralPrior()
    {
        Assert.Equal(12.5 / 15, ReasonletSelector.SuccessRate(Metrics(10, 10), null, 5, 1).Rate, Tolerance);
        Assert.Equal(3.5 / 6, ReasonletSelector.SuccessRate(Metrics(1, 1), null, 5, 1).Rate, Tolerance);
    }

    [Fact]
    public void SuccessRateStartsFromTheParentRecord()
    {
        var parent = Metrics(100, 100);
        Assert.Equal(102.5 / 105, ReasonletSelector.SuccessRate(Metrics(0, 0), parent, 5, 1).Rate, Tolerance);
        Assert.Equal(5 * (102.5 / 105) / 7, ReasonletSelector.SuccessRate(Metrics(2, 0), parent, 5, 1).Rate, Tolerance);
    }

    [Fact]
    public void AFailingParentDoesNotPullAVersionBelowNeutral()
    {
        // The parent's own rate is 2.5 / 25 = 0.1, below neutral, so the version starts from 0.5 like any new procedure.
        var parent = Metrics(20, 0);
        Assert.Equal((1 + (5 * 0.5)) / 6, ReasonletSelector.SuccessRate(Metrics(1, 1), parent, 5, 1).Rate, Tolerance);
        Assert.Equal(0.5, ReasonletSelector.SuccessRate(Metrics(0, 0), parent, 5, 1).Rate, Tolerance);
    }

    [Fact]
    public void ANeutralParentGivesTheNeutralPrior() =>
        Assert.Equal(0.5, ReasonletSelector.SuccessRate(Metrics(0, 0), Metrics(0, 0), 5, 1).Rate, Tolerance);

    [Fact]
    public void AFullAgentWeightReproducesTheUnweightedRate()
    {
        var random = new Random(2404);
        for (var i = 0; i < 200; i++)
        {
            var own = RandomMetrics(random);
            ReasonletMetrics? parent = random.Next(2) == 0 ? null : RandomMetrics(random);
            var k = 0.1 + (random.NextDouble() * 20);
            Assert.Equal(UnweightedRate(own, parent, k), ReasonletSelector.SuccessRate(own, parent, k, 1).Rate);
        }
    }

    [Fact]
    public void AZeroAgentWeightIgnoresAgentOnlyRuns() =>
        Assert.Equal(0.5, ReasonletSelector.SuccessRate(Judged(10, 10, 0, 0), null, 5, 0).Rate);

    [Fact]
    public void AgentOnlyRunsCountAtTheAgentWeight() =>
        Assert.Equal(
            (2 + (0.5 * 6) + (5 * 0.5)) / (4 + (0.5 * 6) + 5),
            ReasonletSelector.SuccessRate(Judged(10, 8, 4, 2), null, 5, 0.5).Rate,
            Tolerance);

    [Fact]
    public void TheParentPriorIsWeightedToo()
    {
        // The parent has four judged successes and six agent-only ones: 4 + 0.5 * 6 = 7 of 7 weighted runs.
        var parentRate = (7 + 2.5) / (7 + 5.0);
        var rate = ReasonletSelector.SuccessRate(Judged(2, 0, 0, 0), Judged(10, 10, 4, 4), 5, 0.5);
        Assert.Equal((0 + (5 * parentRate)) / ((0.5 * 2) + 5), rate.Rate, Tolerance);
        Assert.True(rate.UsesParentPrior);

        // With no weight on agent-only runs the same parent has nothing but its four judged successes.
        var judgedOnly = ReasonletSelector.SuccessRate(Judged(0, 0, 0, 0), Judged(10, 10, 4, 4), 5, 0);
        Assert.Equal((4 + 2.5) / 9, judgedOnly.Rate, Tolerance);
    }

    [Fact]
    public void AParentOfOnlyAgentRunsIsNeutralWithoutAgentWeight()
    {
        var rate = ReasonletSelector.SuccessRate(Metrics(0, 0), Metrics(10, 10), 5, 0);
        Assert.Equal(0.5, rate.Rate);
        Assert.False(rate.UsesParentPrior);
    }

    [Fact]
    public void UsesParentPriorOnlyWhenTheParentBeatsNeutral()
    {
        Assert.False(ReasonletSelector.SuccessRate(Metrics(3, 3), null, 5, 1).UsesParentPrior);
        Assert.False(ReasonletSelector.SuccessRate(Metrics(3, 3), Metrics(0, 0), 5, 1).UsesParentPrior);
        Assert.False(ReasonletSelector.SuccessRate(Metrics(3, 3), Metrics(20, 0), 5, 1).UsesParentPrior);
        Assert.True(ReasonletSelector.SuccessRate(Metrics(3, 3), Metrics(1, 1), 5, 1).UsesParentPrior);
    }

    [Fact]
    public async Task EveryMatchCarriesTheCandidatesOwnEvidence()
    {
        var withParent = Candidate(0.9, Judged(10, 8, 4, 2), Metrics(100, 100));
        var root = Candidate(0.8, Judged(3, 1, 3, 1));
        var weakParent = Candidate(0.7, Metrics(2, 2), Metrics(20, 0));
        var matches = await Rank(Selector(), TestData.Query(), withParent, root, weakParent);
        Assert.Equal(
            [new ReasonletEvidence(10, 4, true), new ReasonletEvidence(3, 3, false), new ReasonletEvidence(2, 0, false)],
            matches.Select(m => m.Evidence));
    }

    [Fact]
    public async Task RankingUsesTheConfiguredAgentWeight()
    {
        var metrics = Judged(10, 8, 4, 2);
        var byDefault = await Rank(Selector(), TestData.Query(), Candidate(0.8, metrics));
        Assert.Equal((2 + 3 + 2.5) / (4 + 3 + 5.0), byDefault[0].Score.SuccessRate, Tolerance);
        var judgedOnly = await Rank(Selector(new ReasonletRankingOptions { AgentVerdictWeight = 0 }), TestData.Query(), Candidate(0.8, metrics));
        Assert.Equal((2 + 2.5) / (4 + 5.0), judgedOnly[0].Score.SuccessRate, Tolerance);
    }

    [Fact]
    public void FreshnessDecaysByHalfLife()
    {
        var half = TimeSpan.FromDays(30);
        Assert.Equal(1.0, ReasonletSelector.Freshness(TimeSpan.Zero, half));
        Assert.Equal(0.5, ReasonletSelector.Freshness(half, half), Tolerance);
        Assert.Equal(0.25, ReasonletSelector.Freshness(half * 2, half), Tolerance);
        Assert.Equal(1.0, ReasonletSelector.Freshness(TimeSpan.FromDays(-3), half));
    }

    [Fact]
    public async Task TheDefaultCutOffDropsWeakMatches()
    {
        var query = TestData.Query();
        Assert.Empty(await Rank(Selector(), query, Candidate(0.49)));
        Assert.Single(await Rank(Selector(), query, Candidate(0.5)));
    }

    [Fact]
    public async Task TheQueryOverridesTheCutOff()
    {
        var query = TestData.Query() with { MinSimilarity = 0.3 };
        Assert.Single(await Rank(Selector(), query, Candidate(0.49)));
        Assert.Empty(await Rank(Selector(), query with { MinSimilarity = 0.6 }, Candidate(0.55)));
    }

    [Fact]
    public async Task ANaNSimilarityIsDropped()
    {
        var kept = Candidate(0.9);
        var matches = await Rank(Selector(), TestData.Query() with { MinSimilarity = 0.0 }, Candidate(double.NaN), kept);
        Assert.Equal([kept.Reasonlet.Id], matches.Select(m => m.Reasonlet.Id));
    }

    [Fact]
    public async Task NoCandidatesGivesAnEmptySuccess() =>
        Assert.Empty(await Rank(Selector(), TestData.Query()));

    [Fact]
    public async Task TopTruncates()
    {
        var query = TestData.Query() with { Top = 2 };
        var matches = await Rank(Selector(), query, Candidate(0.6), Candidate(0.7), Candidate(0.8));
        Assert.Equal(2, matches.Count);
        Assert.Equal(0.8, matches[0].Score.Similarity);
    }

    [Fact]
    public async Task TiesOrderBySimilarityThenId()
    {
        var low = new ReasonletId(new Guid(1, 0, 0, new byte[8]));
        var high = new ReasonletId(new Guid(2, 0, 0, new byte[8]));
        var first = Candidate(0.6, id: high);
        var second = Candidate(0.6, id: low);
        var matches = await Rank(Selector(), TestData.Query(), first, second);
        Assert.Equal([low, high], matches.Select(m => m.Reasonlet.Id));
    }

    [Fact]
    public async Task EqualTotalsFallBackToSimilarity()
    {
        // The success rate has no weight here, so only similarity and freshness count, each at 0.5.
        // The stale candidate scores 0.5 * 1.0 + 0.5 * 0.5 = 0.75 (one half-life old, so freshness 0.5).
        // The fresh candidate scores 0.5 * 0.5 + 0.5 * 1.0 = 0.75. The totals tie, so higher similarity wins.
        var options = new ReasonletRankingOptions { SimilarityWeight = 0.5, SuccessRateWeight = 0.0, FreshnessWeight = 0.5 };
        var stale = Candidate(1.0, Metrics(0, 0, Now - TimeSpan.FromDays(30)));
        var fresh = Candidate(0.5, Metrics(0, 0, Now));
        var matches = await Rank(Selector(options), TestData.Query(), fresh, stale);
        Assert.Equal(stale.Reasonlet.Id, matches[0].Reasonlet.Id);
        Assert.Equal(matches[0].Score.Total, matches[1].Score.Total, Tolerance);
    }

    [Fact]
    public async Task ScoreComponentsFollowTheFormula()
    {
        var metrics = Metrics(4, 3, Now - TimeSpan.FromDays(15));
        var matches = await Rank(Selector(FullAgentWeight), TestData.Query(), Candidate(0.8, metrics));
        var score = matches[0].Score;
        var rate = (3 + 2.5) / 9;
        var freshness = Math.Pow(0.5, 0.5);
        Assert.Equal(0.8, score.Similarity, Tolerance);
        Assert.Equal(rate, score.SuccessRate, Tolerance);
        Assert.Equal(freshness, score.Freshness, Tolerance);
        Assert.Equal((0.5 * 0.8) + (0.3 * rate) + (0.2 * freshness), score.Total, Tolerance);
    }

    [Fact]
    public async Task ComponentsAreClampedIntoTheUnitInterval()
    {
        var matches = await Rank(Selector(), TestData.Query(), Candidate(1.0000000001, Metrics(5, 5)));
        Assert.Equal(1.0, matches[0].Score.Similarity);
        Assert.InRange(matches[0].Score.Total, 0.0, 1.0);
    }

    [Fact]
    public async Task TheBrainstormExampleRanksTheNewVersionFirst()
    {
        var a = Candidate(0.92, Metrics(10, 5));
        var b = Candidate(0.85, Metrics(40, 40));
        var c = Candidate(0.86, Metrics(0, 0), Metrics(40, 40));
        var matches = await Rank(Selector(FullAgentWeight), TestData.Query(), a, b, c);
        Assert.Equal([c.Reasonlet.Id, b.Reasonlet.Id, a.Reasonlet.Id], matches.Select(m => m.Reasonlet.Id));
        Assert.Equal((0.5 * 0.86) + (0.3 * (42.5 / 45)) + 0.2, matches[0].Score.Total, Tolerance);
        Assert.Equal(0.91333333333333, matches[0].Score.Total, 1e-9);
        Assert.Equal(0.90833333333333, matches[1].Score.Total, 1e-9);
        Assert.Equal(0.81, matches[2].Score.Total, Tolerance);
    }

    [Fact]
    public void DefaultOptionsAreValid()
    {
        var options = new ReasonletRankingOptions();
        Assert.True(options.Validate().IsSuccess);
        Assert.Equal(0.5, options.SimilarityWeight);
        Assert.Equal(0.3, options.SuccessRateWeight);
        Assert.Equal(0.2, options.FreshnessWeight);
        Assert.Equal(0.5, options.MinSimilarity);
        Assert.Equal(5, options.PriorWeight);
        Assert.Equal(TimeSpan.FromDays(30), options.FreshnessHalfLife);
        Assert.Equal(0.5, options.AgentVerdictWeight);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void TheAgentWeightBoundsAreValidAndOutsideTheWeightSum(double weight) =>
        Assert.True(new ReasonletRankingOptions { AgentVerdictWeight = weight }.Validate().IsSuccess);

    public static TheoryData<ReasonletRankingOptions, string> InvalidOptions => new()
    {
        { new ReasonletRankingOptions { SimilarityWeight = -0.1, SuccessRateWeight = 0.9, FreshnessWeight = 0.2 }, "SimilarityWeight" },
        { new ReasonletRankingOptions { SimilarityWeight = 1.1, SuccessRateWeight = 0, FreshnessWeight = -0.1 }, "SimilarityWeight" },
        { new ReasonletRankingOptions { SuccessRateWeight = 1.5 }, "SuccessRateWeight" },
        { new ReasonletRankingOptions { FreshnessWeight = double.NaN }, "FreshnessWeight" },
        { new ReasonletRankingOptions { SimilarityWeight = 0.5, SuccessRateWeight = 0.3, FreshnessWeight = 0.3 }, "SimilarityWeight" },
        { new ReasonletRankingOptions { PriorWeight = 0 }, "PriorWeight" },
        { new ReasonletRankingOptions { PriorWeight = double.PositiveInfinity }, "PriorWeight" },
        { new ReasonletRankingOptions { FreshnessHalfLife = TimeSpan.Zero }, "FreshnessHalfLife" },
        { new ReasonletRankingOptions { FreshnessHalfLife = TimeSpan.FromDays(-1) }, "FreshnessHalfLife" },
        { new ReasonletRankingOptions { MinSimilarity = 1.5 }, "MinSimilarity" },
        { new ReasonletRankingOptions { MinSimilarity = -0.5 }, "MinSimilarity" },
        { new ReasonletRankingOptions { AgentVerdictWeight = -0.01 }, "AgentVerdictWeight" },
        { new ReasonletRankingOptions { AgentVerdictWeight = 1.01 }, "AgentVerdictWeight" },
        { new ReasonletRankingOptions { AgentVerdictWeight = double.NaN }, "AgentVerdictWeight" },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptionsFailWithThePropertyPath(ReasonletRankingOptions options, string path)
    {
        var result = options.Validate();
        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith(path + ":", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstructorRejectsBadArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ReasonletSelector(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new ReasonletSelector(new ReasonletRankingOptions(), null!));
        Assert.Throws<ArgumentException>(() => new ReasonletSelector(new ReasonletRankingOptions { PriorWeight = 0 }, TimeProvider.System));
    }
}
