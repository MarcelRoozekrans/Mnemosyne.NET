using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

// Phase 5.3 builds on these public names only: a rename or a signature change must break this build.
public sealed class VariantExplorationSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ReasonletCandidate Candidate(ReasonletId lineage, double similarity, int id) =>
        new()
        {
            LineageId = lineage,
            Reasonlet = TestData.Definition() with { Id = new ReasonletId(new Guid(id, 0, 0, new byte[8])) },
            Similarity = similarity,
            Metrics = new ReasonletMetrics(0, 0, null, Now),
        };

    private static async Task<int> RankedCount(IReasonletSelector selector)
    {
        var lineage = new ReasonletId(new Guid(100, 0, 0, new byte[8]));
        var result = await selector.RankAsync(
            TestData.Query() with { Top = 10 },
            [Candidate(lineage, 0.9, 1), Candidate(lineage, 0.8, 2)],
            TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        return result.Value.Count;
    }

    [Fact]
    public async Task TheOptionsTheThreeArgumentConstructorAndTheOptionsOverloadWorkTogether()
    {
        var ranking = new ReasonletRankingOptions();
        var options = new VariantSelectionOptions { Exploration = VariantExploration.Thompson, Seed = 7, MinTrials = 3 };
        Assert.True(options.Validate().IsSuccess);
        IReasonletSelector constructed = new VariantSelector(new ReasonletSelector(ranking, new FixedClock()), options, ranking);

        var services = new ServiceCollection();
        services.AddSingleton<IReasonletSelector>(new ReasonletSelector(ranking, new FixedClock()));
        IServiceCollection returned = services.AddMnemosyneVariantSelection(options);
        Assert.Same(services, returned);
        using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<IReasonletSelector>();

        Assert.IsType<VariantSelector>(registered);
        Assert.Equal(1, await RankedCount(constructed));
        Assert.Equal(1, await RankedCount(registered));
    }
}
