using Xunit;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

public sealed class VariantSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSelector(IReasonletSelector inner) : IReasonletSelector
    {
        public ReasonletQuery? Seen { get; private set; }

        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
        {
            Seen = query;
            return inner.RankAsync(query, candidates, ct);
        }
    }

    private sealed class FailingSelector(MnemosyneError error) : IReasonletSelector
    {
        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct) =>
            ValueTask.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(error));
    }

    private static ReasonletSelector Inner() => new(new ReasonletRankingOptions(), new FixedTimeProvider(Now));

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
        var result = await new VariantSelector(Inner()).RankAsync(query, candidates, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    [Fact]
    public async Task ItKeepsTheBestVersionOfEachLineage()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var weak = Candidate(a, 0.6);
        var best = Candidate(a, 0.95);
        var middle = Candidate(a, 0.8);
        var other = Candidate(b, 0.7);

        var matches = await Rank(TestData.Query() with { Top = 10 }, weak, best, middle, other);

        Assert.Equal([best.Reasonlet.Id, other.Reasonlet.Id], matches.Select(m => m.Reasonlet.Id));
        Assert.Equal([a, b], matches.Select(m => m.LineageId));
    }

    [Fact]
    public async Task TopAppliesAfterGrouping()
    {
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());

        var matches = await Rank(
            TestData.Query() with { Top = 2 },
            Candidate(a, 0.99), Candidate(a, 0.98), Candidate(a, 0.97), Candidate(b, 0.9), Candidate(c, 0.8));

        Assert.Equal([a, b], matches.Select(m => m.LineageId));
    }

    [Fact]
    public async Task ATopOfOneReturnsTheBestLineageOnly()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());

        var matches = await Rank(TestData.Query() with { Top = 1 }, Candidate(b, 0.7), Candidate(a, 0.9), Candidate(a, 0.85));

        var only = Assert.Single(matches);
        Assert.Equal(a, only.LineageId);
    }

    [Fact]
    public async Task ALineageWhoseVariantsAreAllBelowTheCutOffIsDropped()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var query = TestData.Query() with { Top = 10, MinSimilarity = 0.5 };

        var matches = await Rank(query, Candidate(a, 0.1), Candidate(a, 0.2), Candidate(b, 0.9));

        var only = Assert.Single(matches);
        Assert.Equal(b, only.LineageId);
    }

    [Fact]
    public async Task NoCandidatesGiveAnEmptySuccess()
    {
        var matches = await Rank(TestData.Query());

        Assert.Empty(matches);
    }

    [Fact]
    public async Task InterleavedLineagesKeepTheInnerOrder()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var candidates = new[] { Candidate(a, 0.6), Candidate(b, 0.9), Candidate(a, 0.8), Candidate(b, 0.5) };
        var query = TestData.Query() with { Top = 10 };

        var matches = await Rank(query, candidates);

        Assert.Equal([b, a], matches.Select(m => m.LineageId));
        Assert.Equal([candidates[1].Reasonlet.Id, candidates[2].Reasonlet.Id], matches.Select(m => m.Reasonlet.Id));
    }

    [Fact]
    public async Task EqualScoresStayInTheInnerOrder()
    {
        var candidates = new[] { Candidate(ReasonletId.New(), 0.8), Candidate(ReasonletId.New(), 0.8), Candidate(ReasonletId.New(), 0.8) };
        var query = TestData.Query() with { Top = 10 };
        var direct = await Inner().RankAsync(query, candidates, CancellationToken.None);
        Assert.True(direct.IsSuccess);

        var matches = await Rank(query, candidates);

        Assert.Equal(direct.Value.Select(m => m.Reasonlet.Id), matches.Select(m => m.Reasonlet.Id));
    }

    [Fact]
    public async Task ItAsksTheInnerSelectorForEveryCandidate()
    {
        var recording = new RecordingSelector(Inner());
        var candidates = new[] { Candidate(ReasonletId.New(), 0.9), Candidate(ReasonletId.New(), 0.8), Candidate(ReasonletId.New(), 0.7) };

        await new VariantSelector(recording).RankAsync(TestData.Query() with { Top = 1 }, candidates, CancellationToken.None);

        Assert.Equal(3, recording.Seen!.Top);
    }

    [Fact]
    public async Task ItAsksTheInnerSelectorForAtLeastOneMatch()
    {
        var recording = new RecordingSelector(Inner());

        await new VariantSelector(recording).RankAsync(TestData.Query(), [], CancellationToken.None);

        Assert.Equal(1, recording.Seen!.Top);
    }

    [Fact]
    public async Task AnInnerFailureComesBackUnchanged()
    {
        var error = new MnemosyneError(MnemosyneErrorCode.Unavailable, "down");

        var result = await new VariantSelector(new FailingSelector(error)).RankAsync(TestData.Query(), [], CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public async Task ACancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new VariantSelector(Inner()).RankAsync(TestData.Query(), [Candidate(ReasonletId.New(), 0.9)], cts.Token));
    }

    [Fact]
    public void ANullInnerThrows() =>
        Assert.Throws<ArgumentNullException>(() => new VariantSelector(null!));
}
