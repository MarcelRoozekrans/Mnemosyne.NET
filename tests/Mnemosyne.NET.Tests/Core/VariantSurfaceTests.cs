using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

// Phases 5.2 and 5.3 build on these public names only: a rename or a signature change must break this build.
public sealed class VariantSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ReasonletCandidate Candidate(ReasonletId lineage, double similarity) =>
        new()
        {
            LineageId = lineage,
            Reasonlet = TestData.Definition() with { Id = ReasonletId.New() },
            Similarity = similarity,
            Metrics = new ReasonletMetrics(0, 0, null, Now),
        };

    private static async Task<int> RankedCount(IReasonletSelector selector)
    {
        var lineage = ReasonletId.New();
        var result = await selector.RankAsync(
            TestData.Query() with { Top = 10 },
            [Candidate(lineage, 0.9), Candidate(lineage, 0.8)],
            TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        return result.Value.Count;
    }

    [Fact]
    public async Task TheConstructorAndTheRegistrationBothYieldASelectorThatKeepsOneVersionPerLineage()
    {
        IReasonletSelector constructed = new VariantSelector(new ReasonletSelector(new ReasonletRankingOptions(), new FixedClock()));

        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(new FakeStore());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        IServiceCollection returned = services.AddMnemosyne();
        Assert.Same(services, returned.AddMnemosyneVariantSelection());
        using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<IReasonletSelector>();

        Assert.IsType<VariantSelector>(registered);
        Assert.Equal(1, await RankedCount(constructed));
        Assert.Equal(1, await RankedCount(registered));
    }
}
