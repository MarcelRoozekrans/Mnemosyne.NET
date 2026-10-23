using Microsoft.Extensions.DependencyInjection;
using Minos;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Minos;
using Xunit;

namespace Mnemosyne.NET.Tests.Minos;

public sealed class MinosRegistrationTests
{
    [Fact]
    public void RegisteringResolvesTheEvaluatorAsOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionClient>(FakeDecisionClient.Answering(0.9));
        services.AddMnemosyneMinosEvaluator();
        using var provider = services.BuildServiceProvider();

        var evaluator = provider.GetRequiredService<IReasonletEvaluator>();

        Assert.IsType<MinosReasonletEvaluator>(evaluator);
        Assert.Same(evaluator, provider.GetRequiredService<IReasonletEvaluator>());
    }

    [Fact]
    public async Task TheGivenOptionsAreUsed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionClient>(FakeDecisionClient.Answering(0.9));
        services.AddMnemosyneMinosEvaluator(new MinosEvaluatorOptions { EvaluatorName = "custom" });
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(MinosTestData.Request(), TestContext.Current.CancellationToken);

        Assert.Equal("custom", verdict.Value.Evaluator);
    }

    [Fact]
    public async Task AKeyedClientIsPickedWhenTheKeyIsSet()
    {
        var unkeyed = FakeDecisionClient.Answering(0.1);
        var keyed = FakeDecisionClient.Answering(0.9);
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionClient>(unkeyed);
        services.AddKeyedSingleton<IDecisionClient>("judge", keyed);
        services.AddMnemosyneMinosEvaluator(new MinosEvaluatorOptions { DecisionClientKey = "judge" });
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(MinosTestData.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0.9, verdict.Value.Score);
        Assert.Empty(unkeyed.Requests);
        Assert.Single(keyed.Requests);
    }

    [Fact]
    public async Task TheUnkeyedClientIsPickedWhenNoKeyIsSet()
    {
        var unkeyed = FakeDecisionClient.Answering(0.1);
        var keyed = FakeDecisionClient.Answering(0.9);
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionClient>(unkeyed);
        services.AddKeyedSingleton<IDecisionClient>("judge", keyed);
        services.AddMnemosyneMinosEvaluator();
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(MinosTestData.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0.1, verdict.Value.Score);
        Assert.Empty(keyed.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMissingClientThrowsOnResolve(bool keyed)
    {
        var services = new ServiceCollection();
        services.AddMnemosyneMinosEvaluator(new MinosEvaluatorOptions { DecisionClientKey = keyed ? "judge" : null });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IReasonletEvaluator>());
    }

    [Fact]
    public void InvalidOptionsThrowAtRegistration()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMnemosyneMinosEvaluator(new MinosEvaluatorOptions { SuccessThreshold = 2 }));

        Assert.Equal("options", error.ParamName);
    }

    [Fact]
    public void ANullServiceCollectionThrows() =>
        Assert.Throws<ArgumentNullException>(() => global::Mnemosyne.NET.Minos.ServiceCollectionExtensions.AddMnemosyneMinosEvaluator(null!));
}
