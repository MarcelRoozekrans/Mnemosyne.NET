using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

public sealed class ServiceCollectionTests
{
    private readonly FakeStore _store = new();

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        return services;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    [Fact]
    public async Task TheFacadeResolvesAsOneSingletonAndRecalls()
    {
        using var provider = Services().AddMnemosyne().BuildServiceProvider();

        var memory = provider.GetRequiredService<IReasonletMemory>();

        Assert.IsType<ReasonletMemory>(memory);
        Assert.Same(memory, provider.GetRequiredService<IReasonletMemory>());
        var result = await memory.RecallAsync(TestData.Query(), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(20, _store.Searched!.Value.Limit);
    }

    [Fact]
    public void TheDefaultsAreRegistered()
    {
        using var provider = Services().AddMnemosyne().BuildServiceProvider();

        Assert.IsType<ReasonletSelector>(provider.GetRequiredService<IReasonletSelector>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Equal(20, provider.GetRequiredService<MnemosyneOptions>().CandidatePool);
    }

    [Fact]
    public async Task TheGivenOptionsAreUsed()
    {
        var options = new MnemosyneOptions { CandidatePool = 7 };
        using var provider = Services().AddMnemosyne(options).BuildServiceProvider();

        await provider.GetRequiredService<IReasonletMemory>().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Same(options, provider.GetRequiredService<MnemosyneOptions>());
        Assert.Equal(7, _store.Searched!.Value.Limit);
    }

    [Fact]
    public async Task AHostRegisteredSelectorIsKept()
    {
        var selector = new FakeSelector();
        var services = Services();
        services.AddSingleton<IReasonletSelector>(selector);
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        await provider.GetRequiredService<IReasonletMemory>().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Same(selector, provider.GetRequiredService<IReasonletSelector>());
        Assert.Equal(1, selector.Calls);
    }

    [Fact]
    public void AHostRegisteredTimeProviderIsKept()
    {
        var time = new FixedTimeProvider();
        var services = Services();
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        Assert.Same(time, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task ARegisteredExtractorIsUsed()
    {
        var extractor = FakeIntentExtractor.Answering("extracted", 1.0);
        var services = Services();
        services.AddSingleton<IIntentExtractor>(extractor);
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        await provider.GetRequiredService<IReasonletMemory>().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Single(extractor.Questions);
        Assert.Equal("extracted", _store.Searched!.Value.Intent);
    }

    [Fact]
    public async Task AMissingExtractorIsFine()
    {
        using var provider = Services().AddMnemosyne().BuildServiceProvider();

        await provider.GetRequiredService<IReasonletMemory>().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Null(_store.Searched!.Value.Intent);
    }

    [Fact]
    public async Task RegisteringTwiceGivesTheSelectorAndTheFacadeTheSameOptions()
    {
        // The first options would cut a 0.5 match and ask for 5 candidates; the second keep it and ask for 9.
        var first = new MnemosyneOptions { CandidatePool = 5, Ranking = new ReasonletRankingOptions { MinSimilarity = 0.9 } };
        var second = new MnemosyneOptions { CandidatePool = 9, Ranking = new ReasonletRankingOptions { MinSimilarity = 0.1 } };
        _store.SearchResult = ZeroAlloc.Results.Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success(
            [new ReasonletCandidate(TestData.Definition(), 0.5, new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch))]);
        using var provider = Services().AddMnemosyne(first).AddMnemosyne(second).BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal(9, _store.Searched!.Value.Limit);
        Assert.Single(result.Value);
    }

    [Fact]
    public void InvalidOptionsThrowAtRegistration()
    {
        var error = Assert.Throws<ArgumentException>(
            "options", () => new ServiceCollection().AddMnemosyne(new MnemosyneOptions { Ranking = new ReasonletRankingOptions { PriorWeight = 0 } }));

        Assert.Contains("Ranking.PriorWeight", error.Message);
    }

    [Fact]
    public void NullServicesAreRefused() =>
        Assert.Throws<ArgumentNullException>("services", () => ServiceCollectionExtensions.AddMnemosyne(null!));
}
