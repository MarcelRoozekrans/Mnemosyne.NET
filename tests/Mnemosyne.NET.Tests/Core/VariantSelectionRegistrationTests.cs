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
}
