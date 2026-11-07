using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>A new version of a lineage overtakes a proven incumbent when the selector explores, and never does without.</summary>
public sealed class VariantSimulationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReasonletRankingOptions Ranking = new();

    private const int Rounds = 300;
    private const double IncumbentRate = 0.7;
    private const double ChildRate = 0.9;

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // One recall per round with the incumbent at 200 runs and a child with none. The outcome generator is seeded, and
    // the question carries the round number: a seeded draw is fixed per seed, question and lineage, so a constant
    // question would repeat the same draw whenever the metrics repeat. Returns, per round, whether the child was chosen.
    private static async Task<bool[]> Simulate(VariantSelectionOptions options, int outcomeSeed)
    {
        var selector = new VariantSelector(new ReasonletSelector(Ranking, new FixedTimeProvider()), options, Ranking);
        var outcomes = new Random(outcomeSeed);
        var lineage = ReasonletId.New();
        // Fixed ids, the incumbent's the lower. A new version with no runs of its own scores exactly as its parent does,
        // so without exploration the two tie on score and similarity and the lowest id wins. Random ids would make that
        // tie-break, and with it this test, depend on the clock.
        var incumbentId = new ReasonletId(new Guid("00000000-0000-0000-0000-000000000001"));
        var childId = new ReasonletId(new Guid("00000000-0000-0000-0000-000000000002"));
        var incumbent = new ReasonletMetrics(200, 140, null, Now);
        var child = new ReasonletMetrics(0, 0, null, Now);
        var chosen = new bool[Rounds];

        for (var round = 0; round < Rounds; round++)
        {
            var candidates = new[]
            {
                new ReasonletCandidate
                {
                    LineageId = lineage,
                    Reasonlet = TestData.Definition() with { Id = incumbentId },
                    Similarity = 0.8,
                    Metrics = incumbent,
                },
                new ReasonletCandidate
                {
                    LineageId = lineage,
                    Reasonlet = TestData.Definition() with { Id = childId },
                    Similarity = 0.8,
                    Metrics = child,
                    ParentMetrics = incumbent,
                },
            };
            var query = TestData.Query() with { Question = $"how do I rebase {round}", Top = 10 };

            var result = await selector.RankAsync(query, candidates, TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            var pick = Assert.Single(result.Value).Reasonlet.Id;
            chosen[round] = pick == childId;
            var success = outcomes.NextDouble() < (chosen[round] ? ChildRate : IncumbentRate);
            if (chosen[round])
            {
                child = Next(child, success);
            }
            else
            {
                incumbent = Next(incumbent, success);
            }
        }

        return chosen;
    }

    private static ReasonletMetrics Next(ReasonletMetrics metrics, bool success) =>
        new(metrics.Runs + 1, metrics.Successes + (success ? 1 : 0), null, Now);

    // Observed child picks in the first 20 and the last 100 rounds: seed 11 = 14 and 100, seed 23 = 17 and 100,
    // seed 42 = 15 and 100. The thresholds, 1 and 60, leave a wide margin: the child's 0.9 against the incumbent's 0.7
    // makes it win nearly every round once it has been tried a few times.
    [Theory]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(42)]
    public async Task ThompsonLetsTheNewVersionOvertakeTheIncumbent(int seed)
    {
        var options = new VariantSelectionOptions { Exploration = VariantExploration.Thompson, Seed = seed };

        var chosen = await Simulate(options, seed);

        Assert.True(chosen.Take(20).Count(c => c) >= 1, "the child was never tried in the first 20 rounds");
        Assert.True(chosen.Skip(Rounds - 100).Count(c => c) >= 60, "the child did not take over the last 100 rounds");
    }

    [Fact]
    public async Task WithoutExplorationTheNewVersionIsNeverChosen()
    {
        var chosen = await Simulate(new VariantSelectionOptions(), 11);

        Assert.DoesNotContain(true, chosen);
    }
}
