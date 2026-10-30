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

    private static ReasonletRun JudgeableRun() => new()
    {
        Scope = new ReasonletScope("scope"),
        Id = ReasonletId.New(),
        Outcome = new ReasonletOutcome(false, TimeSpan.FromSeconds(1), 0.5),
        Provenance = TestData.Provenance(),
        Question = "question",
        Answer = "answer",
    };

    [Fact]
    public async Task ARegisteredEvaluatorIsUsed()
    {
        var evaluator = new FakeEvaluator();
        var services = Services();
        services.AddSingleton<IReasonletEvaluator>(evaluator);
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(JudgeableRun(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(evaluator.Requests);
        Assert.Equal(FakeEvaluator.Verdict, _store.Executed!.Value.Evaluation);
    }

    [Fact]
    public async Task TheRegisteredClockTimesTheJudge()
    {
        var clock = new ManualTimeProvider();
        var services = Services();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IReasonletEvaluator>(new WaitingEvaluator(clock.Fire));
        using var provider = services.AddMnemosyne(new MnemosyneOptions { EvaluationTimeout = TimeSpan.FromSeconds(3) }).BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(JudgeableRun(), CancellationToken.None);

        Assert.Equal(ReasonletVerdictSource.Agent, result.Value.Source);
        Assert.Contains(TimeSpan.FromSeconds(3), clock.DueTimes);
        Assert.Null(_store.Executed!.Value.Evaluation);
    }

    [Fact]
    public async Task AMissingEvaluatorLeavesRunsUnjudged()
    {
        using var provider = Services().AddMnemosyne().BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(JudgeableRun(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(_store.Got);
        Assert.Null(_store.Executed!.Value.Evaluation);
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
    public void AMissingStoreThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IReasonletMemory>());

        Assert.Contains(nameof(IReasonletStore), error.Message);
    }

    [Fact]
    public void AMissingEmbeddingGeneratorThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IReasonletMemory>());

        Assert.Contains("IEmbeddingGenerator", error.Message);
    }

    [Fact]
    public void AScopedEmbeddingGeneratorFailsUnderScopeValidation()
    {
        // The facade is a singleton, so whatever it resolves must be one too.
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddScoped<IEmbeddingGenerator<string, Embedding<float>>, FakeEmbeddingGenerator>();
        using var provider = services.AddMnemosyne().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IReasonletMemory>());
    }

    [Fact]
    public void AScopedEvaluatorFailsUnderScopeValidation()
    {
        var services = Services();
        services.AddScoped<IReasonletEvaluator, FakeEvaluator>();
        using var provider = services.AddMnemosyne().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IReasonletMemory>());
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

    [Fact]
    public async Task AddMnemosyneUsesARegisteredGuard()
    {
        var guard = new FakeCaptureGuard { Respond = () => CaptureVerdict.Block("fake-guard", "pii.email") };
        var services = Services();
        services.AddSingleton<ICaptureGuard>(guard);
        using var provider = services.AddMnemosyne().BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>().RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal(1, guard.Calls);
    }

    [Fact]
    public async Task AddMnemosyneWorksWithoutAGuard()
    {
        using var provider = Services().AddMnemosyne().BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>().RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
