using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

public sealed class VariantSelectionRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HostSelector : IReasonletSelector
    {
        public int Calls { get; private set; }

        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
        {
            Calls++;
            return Real.RankAsync(query, candidates, ct);
        }
    }

    private sealed class DisposableSelector : IReasonletSelector, IDisposable
    {
        public static int Disposed;

        public void Dispose() => Disposed++;

        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct) =>
            Real.RankAsync(query, candidates, ct);
    }

    private sealed class TypedSelector(TimeProvider clock) : IReasonletSelector
    {
        public TimeProvider Clock { get; } = clock;

        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
            ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct) =>
            Real.RankAsync(query, candidates, ct);
    }

    private static readonly ReasonletSelector Real = new(new ReasonletRankingOptions(), new FixedClock());

    private static ServiceCollection Services() => new();

    private static ReasonletCandidate Candidate(ReasonletId lineage, double similarity) =>
        new()
        {
            LineageId = lineage,
            Reasonlet = TestData.Definition() with { Id = ReasonletId.New() },
            Similarity = similarity,
            Metrics = new ReasonletMetrics(0, 0, null, Now),
        };

    private static IReasonletSelector Inner(IReasonletSelector selector) => ((VariantSelector)selector).Inner;

    private static async Task<int> Rank(IReasonletSelector selector)
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
    public async Task AfterAddMnemosyneTheSelectorIsAVariantSelectorOverTheDefaultAndGroups()
    {
        var services = Services();
        services.AddSingleton<IReasonletStore>(new FakeStore());
        services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(new FakeEmbeddingGenerator());
        using var provider = services.AddMnemosyne().AddMnemosyneVariantSelection().BuildServiceProvider();

        var selector = provider.GetRequiredService<IReasonletSelector>();

        Assert.IsType<VariantSelector>(selector);
        Assert.IsType<ReasonletSelector>(Inner(selector));
        Assert.Equal(1, await Rank(selector));
    }

    [Fact]
    public async Task AHostSelectorInstanceIsWrapped()
    {
        var host = new HostSelector();
        var services = Services();
        services.AddSingleton<IReasonletSelector>(host);
        using var provider = services.AddMnemosyneVariantSelection().BuildServiceProvider();

        var selector = provider.GetRequiredService<IReasonletSelector>();

        Assert.Same(host, Inner(selector));
        Assert.Equal(1, await Rank(selector));
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public void AHostSelectorFactoryIsWrappedAndKeepsItsLifetime()
    {
        var services = Services();
        services.AddScoped<IReasonletSelector>(_ => new HostSelector());
        using var provider = services.AddMnemosyneVariantSelection().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<IReasonletSelector>();

        Assert.IsType<HostSelector>(Inner(first));
        Assert.Same(first, scope.ServiceProvider.GetRequiredService<IReasonletSelector>());
        using var other = provider.CreateScope();
        Assert.NotSame(first, other.ServiceProvider.GetRequiredService<IReasonletSelector>());
    }

    [Fact]
    public void AHostSelectorTypeIsBuiltWithItsDependencies()
    {
        var clock = new FixedClock();
        var services = Services();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IReasonletSelector, TypedSelector>();
        using var provider = services.AddMnemosyneVariantSelection().BuildServiceProvider();

        var inner = Assert.IsType<TypedSelector>(Inner(provider.GetRequiredService<IReasonletSelector>()));

        Assert.Same(clock, inner.Clock);
    }

    [Fact]
    public void TheLastUnkeyedSelectorIsWrappedAndAKeyedOneIsLeftAlone()
    {
        var first = new HostSelector();
        var last = new HostSelector();
        var keyed = new HostSelector();
        var services = Services();
        services.AddSingleton<IReasonletSelector>(first);
        services.AddSingleton<IReasonletSelector>(last);
        services.AddKeyedSingleton<IReasonletSelector>("k", keyed);
        using var provider = services.AddMnemosyneVariantSelection().BuildServiceProvider();

        Assert.Same(last, Inner(provider.GetRequiredService<IReasonletSelector>()));
        Assert.Same(keyed, provider.GetRequiredKeyedService<IReasonletSelector>("k"));
        Assert.Same(first, provider.GetServices<IReasonletSelector>().OfType<HostSelector>().First());
    }

    [Fact]
    public void WithoutASelectorItThrowsAndNamesAddMnemosyne()
    {
        var services = Services();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddMnemosyneVariantSelection());

        Assert.Contains("AddMnemosyne", ex.Message);
    }

    [Fact]
    public void ASecondCallLeavesOneWrapper()
    {
        var host = new HostSelector();
        var services = Services();
        services.AddSingleton<IReasonletSelector>(host);
        services.AddMnemosyneVariantSelection().AddMnemosyneVariantSelection();
        using var provider = services.BuildServiceProvider();

        var selector = provider.GetRequiredService<IReasonletSelector>();

        Assert.Same(host, Inner(selector));
        Assert.Single(services, d => d.ServiceType == typeof(IReasonletSelector) && !d.IsKeyedService);
    }

    [Fact]
    public void ANullServicesThrows() =>
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddMnemosyneVariantSelection(null!));

    [Fact]
    public void ADisposableHostSelectorRegisteredByTypeIsDisposedWithTheProvider()
    {
        DisposableSelector.Disposed = 0;
        var services = Services();
        services.AddSingleton<IReasonletSelector, DisposableSelector>();
        var provider = services.AddMnemosyneVariantSelection().BuildServiceProvider();
        _ = provider.GetRequiredService<IReasonletSelector>();

        provider.Dispose();

        Assert.Equal(1, DisposableSelector.Disposed);
    }

    [Fact]
    public void ADisposableHostSelectorRegisteredByFactoryIsDisposedWithTheProvider()
    {
        DisposableSelector.Disposed = 0;
        var services = Services();
        services.AddSingleton<IReasonletSelector>(_ => new DisposableSelector());
        var provider = services.AddMnemosyneVariantSelection().BuildServiceProvider();
        _ = provider.GetRequiredService<IReasonletSelector>();

        provider.Dispose();

        Assert.Equal(1, DisposableSelector.Disposed);
    }

    private static IReadOnlyList<int> Picks(IReasonletSelector selector, IReadOnlyList<ReasonletCandidate> candidates)
    {
        var picks = new List<int>();
        for (var round = 0; round < 20; round++)
        {
            var result = selector.RankAsync(TestData.Query() with { Top = 10, Question = "question " + round }, candidates, TestContext.Current.CancellationToken)
                .AsTask().GetAwaiter().GetResult();
            Assert.True(result.IsSuccess);
            picks.Add(candidates.ToList().FindIndex(c => c.Reasonlet.Id == result.Value[0].Reasonlet.Id));
        }

        return picks;
    }

    private static List<ReasonletCandidate> Variants()
    {
        var lineage = ReasonletId.New();
        return Enumerable.Range(0, 8).Select(n => Candidate(lineage, 0.9 - (n * 0.01))).ToList();
    }

    private static ServiceProvider SeededProvider(MnemosyneOptions? mnemosyne = null)
    {
        var services = Services();
        if (mnemosyne is not null)
        {
            services.AddSingleton(mnemosyne);
        }

        services.AddSingleton<IReasonletSelector>(new HostSelector());
        services.AddMnemosyneVariantSelection(new VariantSelectionOptions { Exploration = VariantExploration.Thompson, Seed = 42 });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheOptionsReachTheSelector()
    {
        var variants = Variants();
        using var first = SeededProvider();
        using var second = SeededProvider();

        var a = Picks(first.GetRequiredService<IReasonletSelector>(), variants);
        var b = Picks(second.GetRequiredService<IReasonletSelector>(), variants);

        Assert.Equal(a, b);
        Assert.True(a.Distinct().Count() > 1, "Thompson sampling with equal metrics should vary with the question");
    }

    [Fact]
    public void TheRankingOptionsComeFromTheRegisteredMnemosyneOptions()
    {
        var bad = new MnemosyneOptions { Ranking = new ReasonletRankingOptions { PriorWeight = -1 } };
        using (var provider = SeededProvider(bad))
        {
            Assert.Throws<ArgumentException>(() => provider.GetRequiredService<IReasonletSelector>());
        }

        var variants = Variants();
        using var defaults = SeededProvider(new MnemosyneOptions());
        using var heavy = SeededProvider(new MnemosyneOptions { Ranking = new ReasonletRankingOptions { PriorWeight = 500 } });

        Assert.NotEqual(
            Picks(defaults.GetRequiredService<IReasonletSelector>(), variants),
            Picks(heavy.GetRequiredService<IReasonletSelector>(), variants));
    }

    [Fact]
    public void InvalidOptionsThrowAtRegistration()
    {
        var services = Services();
        services.AddSingleton<IReasonletSelector>(new HostSelector());

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddMnemosyneVariantSelection(new VariantSelectionOptions { MinTrials = -1 }));

        Assert.Equal("options", ex.ParamName);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(VariantSelectionMarker));
    }

    [Fact]
    public void NullOptionsThrow()
    {
        var services = Services();
        services.AddSingleton<IReasonletSelector>(new HostSelector());

        Assert.Throws<ArgumentNullException>(() => services.AddMnemosyneVariantSelection(null!));
    }

    [Fact]
    public void ANullServicesThrowsForTheOptionsOverload() =>
        Assert.Throws<ArgumentNullException>(() =>
            ServiceCollectionExtensions.AddMnemosyneVariantSelection(null!, new VariantSelectionOptions()));

    [Fact]
    public void TheFirstCallsOptionsWinWhicheverOverloadComesFirst()
    {
        var variants = Variants();
        var thompson = new VariantSelectionOptions { Exploration = VariantExploration.Thompson, Seed = 1 };

        var firstOnly = Services();
        firstOnly.AddSingleton<IReasonletSelector>(new HostSelector());
        firstOnly.AddMnemosyneVariantSelection(thompson);
        using var expected = firstOnly.BuildServiceProvider();

        var optionsFirst = Services();
        optionsFirst.AddSingleton<IReasonletSelector>(new HostSelector());
        optionsFirst.AddMnemosyneVariantSelection(thompson).AddMnemosyneVariantSelection()
            .AddMnemosyneVariantSelection(new VariantSelectionOptions { Seed = 2 });
        using var optionsFirstProvider = optionsFirst.BuildServiceProvider();

        Assert.Single(optionsFirst, d => d.ServiceType == typeof(IReasonletSelector) && !d.IsKeyedService);
        Assert.Single(optionsFirst, d => d.ServiceType == typeof(VariantSelectionMarker));
        var expectedPicks = Picks(expected.GetRequiredService<IReasonletSelector>(), variants);
        Assert.Equal(expectedPicks, Picks(optionsFirstProvider.GetRequiredService<IReasonletSelector>(), variants));
        Assert.True(expectedPicks.Distinct().Count() > 1);

        var parameterlessFirst = Services();
        parameterlessFirst.AddSingleton<IReasonletSelector>(new HostSelector());
        parameterlessFirst.AddMnemosyneVariantSelection().AddMnemosyneVariantSelection(thompson);
        using var parameterlessProvider = parameterlessFirst.BuildServiceProvider();

        Assert.Single(parameterlessFirst, d => d.ServiceType == typeof(IReasonletSelector) && !d.IsKeyedService);
        Assert.All(Picks(parameterlessProvider.GetRequiredService<IReasonletSelector>(), variants), pick => Assert.Equal(0, pick));
    }

    [Fact]
    public void ASecondCallWithInvalidOptionsStillThrows()
    {
        var services = Services();
        services.AddSingleton<IReasonletSelector>(new HostSelector());
        services.AddMnemosyneVariantSelection();

        Assert.Throws<ArgumentException>(() =>
            services.AddMnemosyneVariantSelection(new VariantSelectionOptions { MinTrials = -1 }));
    }
}
