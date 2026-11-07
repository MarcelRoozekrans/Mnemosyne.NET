using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The Thompson-sampling pick of <see cref="VariantSelector"/>.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class VariantExplorationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReasonletRankingOptions Ranking = new();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSelector(IReasonletSelector inner) : IReasonletSelector
    {
        public ReasonletQuery? Seen { get; private set; }

        public IReadOnlyList<ReasonletCandidate>? SeenCandidates { get; private set; }

        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
        {
            Seen = query;
            SeenCandidates = candidates;
            return inner.RankAsync(query, candidates, ct);
        }
    }

    private sealed class FailingSelector(MnemosyneError error) : IReasonletSelector
    {
        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct) =>
            ValueTask.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(error));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReasonletSelector Inner() => new(Ranking, new FixedTimeProvider(Now));

    private static VariantSelector Thompson(int? seed = 7, int minTrials = 0, IReasonletSelector? inner = null) =>
        new(inner ?? Inner(), new VariantSelectionOptions { Exploration = VariantExploration.Thompson, Seed = seed, MinTrials = minTrials }, Ranking);

    private static ReasonletCandidate Candidate(ReasonletId lineage, int runs, int successes, double similarity = 0.8, ReasonletId? id = null) =>
        new()
        {
            LineageId = lineage,
            Reasonlet = TestData.Definition() with { Id = id ?? ReasonletId.New() },
            Similarity = similarity,
            Metrics = new ReasonletMetrics(runs, successes, null, Now),
        };

    private static async Task<IReadOnlyList<ReasonletMatch>> Rank(
        VariantSelector selector, ReasonletQuery query, params ReasonletCandidate[] candidates)
    {
        var result = await selector.RankAsync(query, candidates, Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static ReasonletQuery Query(string question = "how do I rebase") => TestData.Query() with { Question = question, Top = 10 };

    [Fact]
    public async Task NoneReturnsWhatTheSingleArgumentSelectorReturns()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var candidates = new[]
        {
            Candidate(a, 10, 9, 0.7), Candidate(a, 0, 0, 0.9), Candidate(a, 40, 10, 0.95),
            Candidate(b, 3, 1, 0.6), Candidate(b, 3, 3, 0.6),
        };
        var none = new VariantSelector(Inner(), new VariantSelectionOptions(), Ranking);

        var expected = await Rank(new VariantSelector(Inner()), Query(), candidates);
        var actual = await Rank(none, Query(), candidates);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task NoneAsksTheInnerSelectorForEveryCandidate()
    {
        var recording = new RecordingSelector(Inner());
        var a = ReasonletId.New();
        var candidates = new[] { Candidate(a, 1, 1), Candidate(a, 2, 1), Candidate(ReasonletId.New(), 0, 0) };

        await new VariantSelector(recording, new VariantSelectionOptions(), Ranking).RankAsync(Query() with { Top = 1 }, candidates, Ct);

        Assert.Equal(3, recording.Seen!.Top);
        Assert.Equal(candidates, recording.SeenCandidates);
    }

    [Fact]
    public async Task ASeededRunRepeats()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var candidates = new[]
        {
            Candidate(a, 4, 2), Candidate(a, 4, 3), Candidate(a, 0, 0),
            Candidate(b, 6, 3), Candidate(b, 2, 1),
        };
        var first = await Rank(Thompson(), Query(), candidates);

        for (var i = 0; i < 20; i++)
        {
            var again = await Rank(Thompson(), Query(), candidates);
            Assert.Equal(first.Select(m => m.Reasonlet.Id), again.Select(m => m.Reasonlet.Id));
        }
    }

    [Fact]
    public async Task ASeededRunDoesNotDependOnTheOrderOfTheLineages()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var ofA = new[] { Candidate(a, 1, 1), Candidate(a, 1, 0), Candidate(a, 0, 0) };
        var ofB = new[] { Candidate(b, 2, 1), Candidate(b, 0, 0), Candidate(b, 3, 2) };

        var forward = await Rank(Thompson(), Query(), [.. ofA, .. ofB]);
        var backward = await Rank(Thompson(), Query(), [.. ofB, .. ofA]);

        Assert.Equal(forward.Select(m => m.Reasonlet.Id.Value).Order(), backward.Select(m => m.Reasonlet.Id.Value).Order());
    }

    [Fact]
    public async Task ASeededRunDoesNotDependOnTheOrderOfTheVariantsInALineage()
    {
        var lineage = ReasonletId.New();
        var variants = new[]
        {
            Candidate(lineage, 4, 2), Candidate(lineage, 4, 3), Candidate(lineage, 6, 3),
            Candidate(lineage, 2, 1), Candidate(lineage, 9, 5),
        };
        using var capture = new TelemetryCapture();
        var query = Query();
        var permutations = new int[][] { [0, 1, 2, 3, 4], [4, 3, 2, 1, 0], [2, 0, 4, 1, 3], [1, 2, 3, 4, 0], [3, 4, 0, 2, 1] };

        var runs = new List<(ReasonletId Id, object? Explored)>();
        foreach (var permutation in permutations)
        {
            var shuffled = permutation.Select(i => variants[i]).ToArray();
            var result = await RecallTelemetry.RunAsync(_ => Thompson().RankAsync(query, shuffled, Ct));
            var activity = capture.Activities.Last(x => x.OperationName == RecallTelemetry.RecallActivityName);
            runs.Add((Assert.Single(result.Value).Reasonlet.Id, activity.GetTagItem(RecallTelemetry.VariantExploredTag)));
        }

        Assert.All(runs, run => Assert.Equal(runs[0], run));
    }

    [Fact]
    public async Task TheSameSeedWithAnotherQuestionCanPickAnotherVariant()
    {
        var a = ReasonletId.New();
        var candidates = new[] { Candidate(a, 0, 0), Candidate(a, 0, 0), Candidate(a, 0, 0) };
        var picked = new HashSet<ReasonletId>();

        for (var i = 0; i < 50; i++)
        {
            var matches = await Rank(Thompson(), Query($"question {i}"), candidates);
            picked.Add(Assert.Single(matches).Reasonlet.Id);
        }

        Assert.True(picked.Count > 1);
    }

    [Fact]
    public async Task MinTrialsForcesTheVariantWithFewerRunsAndThenLetsGo()
    {
        var a = ReasonletId.New();
        var (fresh, proven) = (ReasonletId.New(), ReasonletId.New());

        for (var i = 0; i < 20; i++)
        {
            var forced = await Rank(
                Thompson(minTrials: 5), Query($"question {i}"),
                Candidate(a, 50, 45, id: proven), Candidate(a, 2, 0, id: fresh));
            Assert.Equal(fresh, Assert.Single(forced).Reasonlet.Id);
        }

        var picked = new HashSet<ReasonletId>();
        for (var i = 0; i < 50; i++)
        {
            var sampled = await Rank(
                Thompson(minTrials: 5), Query($"question {i}"),
                Candidate(a, 5, 3, id: proven), Candidate(a, 5, 3, id: fresh));
            picked.Add(Assert.Single(sampled).Reasonlet.Id);
        }

        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public async Task WithEveryVariantAtNoRunsMinTrialsPicksTheLowestId()
    {
        var a = ReasonletId.New();
        var low = new ReasonletId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var mid = new ReasonletId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var high = new ReasonletId(Guid.Parse("00000000-0000-0000-0000-000000000003"));

        var matches = await Rank(
            Thompson(minTrials: 1), Query(),
            Candidate(a, 0, 0, id: mid), Candidate(a, 0, 0, id: high), Candidate(a, 0, 0, id: low));

        Assert.Equal(low, Assert.Single(matches).Reasonlet.Id);
    }

    [Fact]
    public async Task ASingleVariantLineageIsReturnedAsIs()
    {
        var candidate = Candidate(ReasonletId.New(), 3, 1);

        var viaThompson = await Rank(Thompson(), Query(), candidate);
        var direct = await Inner().RankAsync(Query(), [candidate], Ct);

        Assert.True(direct.IsSuccess);
        Assert.Equal(direct.Value, viaThompson);
    }

    [Fact]
    public async Task AnUnseededRunDoesNotThrow()
    {
        var a = ReasonletId.New();

        for (var i = 0; i < 20; i++)
        {
            var matches = await Rank(Thompson(seed: null), Query(), Candidate(a, 3, 2), Candidate(a, 1, 0), Candidate(ReasonletId.New(), 0, 0));
            Assert.Equal(2, matches.Count);
        }
    }

    [Fact]
    public async Task TheInnerSelectorSeesOnlyThePicks()
    {
        var recording = new RecordingSelector(Inner());
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var candidates = new[] { Candidate(a, 1, 1), Candidate(a, 2, 1), Candidate(b, 0, 0), Candidate(b, 4, 4), Candidate(b, 9, 1) };

        await Thompson(inner: recording).RankAsync(Query() with { Top = 1 }, candidates, Ct);

        Assert.Equal(2, recording.Seen!.Top);
        Assert.Equal([a, b], recording.SeenCandidates!.Select(c => c.LineageId));
    }

    [Fact]
    public async Task TopAppliesAfterThePick()
    {
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());

        var matches = await Rank(
            Thompson(), Query() with { Top = 2 },
            Candidate(a, 0, 0, 0.99), Candidate(a, 0, 0, 0.99), Candidate(b, 0, 0, 0.9), Candidate(c, 0, 0, 0.5));

        Assert.Equal([a, b], matches.Select(m => m.LineageId));
    }

    [Fact]
    public async Task NoCandidatesAskTheInnerSelectorForAtLeastOneMatch()
    {
        var recording = new RecordingSelector(Inner());

        var result = await Thompson(inner: recording).RankAsync(Query(), [], Ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Equal(1, recording.Seen!.Top);
    }

    [Fact]
    public async Task AnInnerFailureComesBackUnchanged()
    {
        var error = new MnemosyneError(MnemosyneErrorCode.Unavailable, "down");

        var result = await Thompson(inner: new FailingSelector(error)).RankAsync(Query(), [Candidate(ReasonletId.New(), 0, 0)], Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }

    [Theory]
    [InlineData(VariantExploration.None)]
    [InlineData(VariantExploration.Thompson)]
    public async Task ACancelledTokenGivesACanceledValueTaskWithoutThrowingFromTheCall(VariantExploration exploration)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var selector = new VariantSelector(Inner(), new VariantSelectionOptions { Exploration = exploration, Seed = 7 }, Ranking);
        var a = ReasonletId.New();

        var pending = selector.RankAsync(Query(), [Candidate(a, 0, 0), Candidate(a, 1, 1)], cts.Token);

        Assert.True(pending.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task ConcurrentSeededCallsPickTheSameVariants()
    {
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());
        var candidates = new[]
        {
            Candidate(a, 4, 2), Candidate(a, 4, 3), Candidate(a, 1, 0),
            Candidate(b, 6, 3), Candidate(b, 2, 1), Candidate(b, 3, 3),
            Candidate(c, 0, 0), Candidate(c, 1, 1),
        };
        var selector = Thompson();
        var expected = (await Rank(selector, Query(), candidates)).Select(m => m.Reasonlet.Id).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Rank(selector, Query(), candidates), Ct)));

        Assert.All(results, matches => Assert.Equal(expected, matches.Select(m => m.Reasonlet.Id)));
    }

    [Fact]
    public async Task AForcedPickThatIsNotTheBestRateCountsAsExplored()
    {
        using var capture = new TelemetryCapture();
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        var candidates = new[]
        {
            Candidate(a, 200, 180), Candidate(a, 0, 0), Candidate(a, 50, 40),
            Candidate(b, 200, 180), Candidate(b, 100, 20),
        };

        await RecallTelemetry.RunAsync(_ => Thompson(minTrials: 1).RankAsync(Query(), candidates, Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Equal(1, activity.GetTagItem(RecallTelemetry.VariantExploredTag));
        Assert.Equal(2, activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Equal(3, activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task APickOfTheBestRateIsNotExplored()
    {
        using var capture = new TelemetryCapture();
        var a = ReasonletId.New();
        // So far apart that every draw of the strong one beats every draw of the weak one.
        var strong = Candidate(a, 1000, 990);
        var candidates = new[] { Candidate(a, 1000, 10), strong };

        var matches = await RecallTelemetry.RunAsync(_ => Thompson().RankAsync(Query(), candidates, Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Equal(strong.Reasonlet.Id, Assert.Single(matches.Value).Reasonlet.Id);
        Assert.Equal(0, activity.GetTagItem(RecallTelemetry.VariantExploredTag));
    }

    [Fact]
    public async Task AmongEqualRatesTheLowestIdIsTheBestSoPickingItIsNotExplored()
    {
        using var capture = new TelemetryCapture();
        var a = ReasonletId.New();
        var low = new ReasonletId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var high = new ReasonletId(Guid.Parse("00000000-0000-0000-0000-000000000002"));

        await RecallTelemetry.RunAsync(_ =>
            Thompson(minTrials: 1).RankAsync(Query(), [Candidate(a, 0, 0, id: high), Candidate(a, 0, 0, id: low)], Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Equal(0, activity.GetTagItem(RecallTelemetry.VariantExploredTag));
    }

    [Fact]
    public async Task TheLineagesTagCountsTheMatchesReturned()
    {
        using var capture = new TelemetryCapture();
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());
        // Every lineage but c is forced onto its unrun variant, which is not its best rate.
        var candidates = new[]
        {
            Candidate(a, 100, 90, 0.5), Candidate(a, 0, 0, 0.5), Candidate(b, 100, 90, 0.6), Candidate(b, 0, 0, 0.6),
            Candidate(c, 0, 0, 0.9),
        };

        await RecallTelemetry.RunAsync(_ => Thompson(minTrials: 1).RankAsync(Query() with { Top = 1 }, candidates, Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Equal(1, activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Equal(2, activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
        // Explored counts the picks before Top, so it can exceed the lineages returned.
        Assert.Equal(2, activity.GetTagItem(RecallTelemetry.VariantExploredTag));
    }

    [Fact]
    public async Task NoneSetsNoExploredTag()
    {
        using var capture = new TelemetryCapture();
        var a = ReasonletId.New();

        await RecallTelemetry.RunAsync(_ =>
            new VariantSelector(Inner(), new VariantSelectionOptions(), Ranking).RankAsync(Query(), [Candidate(a, 0, 0), Candidate(a, 1, 1)], Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Null(activity.GetTagItem(RecallTelemetry.VariantExploredTag));
        Assert.Equal(1, activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
    }

    [Fact]
    public async Task AFailingInnerSelectorSetsNoTagsInThompsonMode()
    {
        using var capture = new TelemetryCapture();
        var error = new MnemosyneError(MnemosyneErrorCode.Unavailable, "down");

        await RecallTelemetry.RunAsync(_ =>
            Thompson(inner: new FailingSelector(error)).RankAsync(Query(), [Candidate(ReasonletId.New(), 0, 0)], Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Null(activity.GetTagItem(RecallTelemetry.VariantExploredTag));
        Assert.Null(activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
    }

    [Fact]
    public async Task AnotherCurrentActivityIsLeftUntaggedInThompsonMode()
    {
        using var capture = new TelemetryCapture();
        using var other = RecallTelemetry.Source.StartActivity("mnemosyne.other");
        Assert.NotNull(other);
        var a = ReasonletId.New();

        await Thompson().RankAsync(Query(), [Candidate(a, 0, 0), Candidate(a, 1, 1)], Ct);

        Assert.Null(other.GetTagItem(RecallTelemetry.VariantExploredTag));
        Assert.Null(other.GetTagItem(RecallTelemetry.VariantLineagesTag));
    }

    [Fact]
    public void ThePosteriorMeanIsTheSmoothedRate()
    {
        var own = new ReasonletMetrics(12, 7, null, Now, JudgedRuns: 4, JudgedSuccesses: 3);
        var parent = new ReasonletMetrics(30, 27, null, Now, JudgedRuns: 10, JudgedSuccesses: 9);

        foreach (var p in new ReasonletMetrics?[] { null, parent, new ReasonletMetrics(30, 3, null, Now) })
        {
            var (alpha, beta) = ReasonletSelector.Posterior(own, p, Ranking.PriorWeight, Ranking.AgentVerdictWeight);
            var rate = ReasonletSelector.SuccessRate(own, p, Ranking.PriorWeight, Ranking.AgentVerdictWeight).Rate;

            Assert.Equal(rate, alpha / (alpha + beta), 12);
        }
    }

    [Fact]
    public void ThePosteriorCountsOwnRunsOnce()
    {
        // Prior 0.5, k 5, 4 judged runs with 3 successes: Beta(2.5 + 3, 2.5 + 1).
        var (alpha, beta) = ReasonletSelector.Posterior(new ReasonletMetrics(4, 3, null, Now, 4, 3), null, 5.0, 0.5);

        Assert.Equal(5.5, alpha, 12);
        Assert.Equal(3.5, beta, 12);
    }

    [Fact]
    public void ThePosteriorShapesStayAboveZero()
    {
        var (alpha, beta) = ReasonletSelector.Posterior(new ReasonletMetrics(0, 0, null, Now), null, double.Epsilon, 0.5);

        Assert.True(alpha > 0.0);
        Assert.True(beta > 0.0);
    }

    [Fact]
    public void ANullArgumentThrows()
    {
        var options = new VariantSelectionOptions();

        Assert.Throws<ArgumentNullException>(() => new VariantSelector(null!, options, Ranking));
        Assert.Throws<ArgumentNullException>(() => new VariantSelector(Inner(), null!, Ranking));
        Assert.Throws<ArgumentNullException>(() => new VariantSelector(Inner(), options, null!));
    }

    [Fact]
    public void InvalidOptionsThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new VariantSelector(Inner(), new VariantSelectionOptions { MinTrials = -1 }, Ranking));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void InvalidRankingOptionsThrowWhenExploring()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new VariantSelector(
                Inner(),
                new VariantSelectionOptions { Exploration = VariantExploration.Thompson },
                new ReasonletRankingOptions { PriorWeight = 0.0 }));

        Assert.Equal("ranking", exception.ParamName);
    }

    [Fact]
    public async Task InvalidRankingOptionsAreIgnoredWithoutExploration()
    {
        var a = ReasonletId.New();
        var selector = new VariantSelector(Inner(), new VariantSelectionOptions(), new ReasonletRankingOptions { PriorWeight = 0.0 });

        var matches = await Rank(selector, Query(), Candidate(a, 3, 1), Candidate(a, 3, 3));

        Assert.Single(matches);
    }
}
