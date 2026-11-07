using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The tags <see cref="VariantSelector"/> puts on the recall activity.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class VariantTelemetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingSelector : IReasonletSelector
    {
        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct) =>
            ValueTask.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(
                new MnemosyneError(MnemosyneErrorCode.Unavailable, "down")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VariantSelector Selector() =>
        new(new ReasonletSelector(new ReasonletRankingOptions(), new FixedTimeProvider(Now)));

    private static ReasonletCandidate Candidate(ReasonletId lineage, double similarity) =>
        new()
        {
            LineageId = lineage,
            Reasonlet = TestData.Definition() with { Id = ReasonletId.New() },
            Similarity = similarity,
            Metrics = new ReasonletMetrics(0, 0, null, Now),
        };

    private static ReasonletCandidate[] Candidates()
    {
        var (a, b) = (ReasonletId.New(), ReasonletId.New());
        return [Candidate(a, 0.9), Candidate(a, 0.8), Candidate(a, 0.7), Candidate(b, 0.6)];
    }

    private static async Task<Activity> RecallWith(TelemetryCapture capture, IReasonletSelector selector, ReasonletCandidate[] candidates)
    {
        await RecallTelemetry.RunAsync(_ => selector.RankAsync(TestData.Query() with { Top = 10 }, candidates, Ct));
        return Assert.Single(capture.Activities, a => a.OperationName == RecallTelemetry.RecallActivityName);
    }

    [Fact]
    public async Task ARecallIsTaggedWithTheLineagesKeptAndTheVariantsDropped()
    {
        using var capture = new TelemetryCapture();

        var activity = await RecallWith(capture, Selector(), Candidates());

        Assert.Equal(2, activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Equal(2, activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task TheDroppedCountIsTheInnerMatchesMinusTheLineagesKept()
    {
        using var capture = new TelemetryCapture();
        var a = ReasonletId.New();
        // Below the cut-off the inner selector drops it, so it is not a dropped variant.
        var candidates = new[] { Candidate(a, 0.9), Candidate(a, 0.8), Candidate(ReasonletId.New(), 0.0) };

        await RecallTelemetry.RunAsync(_ =>
            Selector().RankAsync(TestData.Query() with { Top = 10, MinSimilarity = 0.5 }, candidates, Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Equal(1, activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Equal(1, activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task ALineageCutByTopIsNotCountedAsDropped()
    {
        using var capture = new TelemetryCapture();
        var (a, b, c) = (ReasonletId.New(), ReasonletId.New(), ReasonletId.New());
        var candidates = new[] { Candidate(a, 0.9), Candidate(a, 0.85), Candidate(b, 0.8), Candidate(c, 0.7), Candidate(c, 0.6) };

        await RecallTelemetry.RunAsync(_ => Selector().RankAsync(TestData.Query() with { Top = 1 }, candidates, Ct));
        var activity = Assert.Single(capture.Activities, x => x.OperationName == RecallTelemetry.RecallActivityName);

        Assert.Equal(1, activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Equal(2, activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task TheTagValuesAreCountsNotStrings()
    {
        using var capture = new TelemetryCapture();

        var activity = await RecallWith(capture, Selector(), Candidates());

        Assert.IsType<int>(activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.IsType<int>(activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task AFailingInnerSelectorSetsNoTags()
    {
        using var capture = new TelemetryCapture();

        var activity = await RecallWith(capture, new VariantSelector(new FailingSelector()), Candidates());

        Assert.Null(activity.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Null(activity.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task AnotherCurrentActivityIsLeftUntagged()
    {
        using var capture = new TelemetryCapture();
        var other = Activity.Current;
        Assert.NotNull(other);
        Assert.NotEqual(RecallTelemetry.RecallActivityName, other.OperationName);

        await Selector().RankAsync(TestData.Query() with { Top = 10 }, Candidates(), Ct);

        Assert.Null(other.GetTagItem(RecallTelemetry.VariantLineagesTag));
        Assert.Null(other.GetTagItem(RecallTelemetry.VariantDroppedTag));
    }

    [Fact]
    public async Task NoCurrentActivityIsFine()
    {
        using var capture = new TelemetryCapture();
        Activity.Current = null;

        var result = await Selector().RankAsync(TestData.Query() with { Top = 10 }, Candidates(), Ct);

        Assert.True(result.IsSuccess);
    }
}
