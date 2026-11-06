using Xunit;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;

namespace Mnemosyne.NET.Tests.Core;

public sealed class LineageVariantSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static LineageVariantSelector Selector() =>
        new(new ReasonletSelector(new ReasonletRankingOptions(), new FixedTimeProvider(Now)));

    private static ReasonletCandidate Candidate(ReasonletId lineage, double similarity) =>
        new()
        {
            LineageId = lineage,
            Reasonlet = TestData.Definition() with { Id = ReasonletId.New() },
            Similarity = similarity,
            Metrics = new ReasonletMetrics(0, 0, null, Now),
        };

    private static async Task<IReadOnlyList<ReasonletMatch>> Rank(ReasonletQuery query, params ReasonletCandidate[] candidates)
    {
        var result = await Selector().RankAsync(query, candidates, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    [Fact]
    public async Task ItReturnsOneMatchPerLineage()
    {
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());
        var query = TestData.Query() with { Top = 10 };

        var matches = await Rank(query, Candidate(a, 0.9), Candidate(a, 0.8), Candidate(a, 0.7), Candidate(b, 0.6), Candidate(c, 0.5));

        Assert.Equal(3, matches.Count);
        Assert.Equal([a, b, c], matches.Select(m => m.LineageId));
    }

    [Fact]
    public async Task ItKeepsTheHighestScoringVersionOfEachLineage()
    {
        var a = ReasonletId.New();
        var weak = Candidate(a, 0.6);
        var best = Candidate(a, 0.95);
        var middle = Candidate(a, 0.8);

        var matches = await Rank(TestData.Query(), weak, best, middle);

        var only = Assert.Single(matches);
        Assert.Equal(best.Reasonlet.Id, only.Reasonlet.Id);
    }

    [Fact]
    public async Task ALineageOfOneIsReturnedUnchanged()
    {
        var a = ReasonletId.New();
        var candidate = Candidate(a, 0.8);

        var viaVariant = await Rank(TestData.Query(), candidate);
        var direct = await new ReasonletSelector(new ReasonletRankingOptions(), new FixedTimeProvider(Now))
            .RankAsync(TestData.Query(), [candidate], CancellationToken.None);

        Assert.True(direct.IsSuccess);
        Assert.Equal(direct.Value, viaVariant);
    }

    [Fact]
    public async Task ItOrdersTheLineagesByTheirChosenScore()
    {
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());
        var query = TestData.Query() with { Top = 10 };

        var matches = await Rank(query, Candidate(a, 0.6), Candidate(b, 0.9), Candidate(a, 0.7), Candidate(c, 0.8));

        Assert.Equal([b, c, a], matches.Select(m => m.LineageId));
    }

    [Fact]
    public async Task ItCutsToTheCallersTop()
    {
        var query = TestData.Query() with { Top = 2 };

        var matches = await Rank(query, Candidate(ReasonletId.New(), 0.9), Candidate(ReasonletId.New(), 0.8), Candidate(ReasonletId.New(), 0.7));

        Assert.Equal(2, matches.Count);
    }
}
