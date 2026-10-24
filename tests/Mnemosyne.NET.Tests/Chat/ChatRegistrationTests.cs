using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

public sealed class ChatRegistrationTests
{
    private const string Verdict = """{"worked":true,"confidence":0.9}""";

    private static ReasonletEvaluationRequest Request() => new()
    {
        Graph = TestData.Graph(),
        Name = "name",
        Intent = "intent",
        Question = "Where is invoice 42?",
        Answer = "It is in the March folder.",
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), 0.5),
    };

    [Fact]
    public void RegisteringResolvesTheEvaluatorAsOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(FakeChatClient.Replying(Verdict));
        services.AddMnemosyneChatEvaluator();
        using var provider = services.BuildServiceProvider();

        var evaluator = provider.GetRequiredService<IReasonletEvaluator>();

        Assert.IsType<ChatReasonletEvaluator>(evaluator);
        Assert.Same(evaluator, provider.GetRequiredService<IReasonletEvaluator>());
    }

    [Fact]
    public async Task TheGivenOptionsAreUsed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(FakeChatClient.Replying(Verdict));
        services.AddMnemosyneChatEvaluator(new ChatEvaluatorOptions { EvaluatorName = "custom" });
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("custom", verdict.Value.Evaluator);
    }

    [Fact]
    public async Task NullOptionsGiveVerdictsNamedLlmJudge()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(FakeChatClient.Replying(Verdict));
        services.AddMnemosyneChatEvaluator(null);
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("llm-judge", verdict.Value.Evaluator);
    }

    [Fact]
    public async Task AKeyedClientIsPickedWhenTheKeyIsSet()
    {
        var unkeyed = FakeChatClient.Replying("""{"worked":true,"confidence":0.1}""");
        var keyed = FakeChatClient.Replying(Verdict);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(unkeyed);
        services.AddKeyedSingleton<IChatClient>("judge", keyed);
        services.AddMnemosyneChatEvaluator(new ChatEvaluatorOptions { ChatClientKey = "judge" });
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0.9, verdict.Value.Score);
        Assert.Empty(unkeyed.Requests);
        Assert.Single(keyed.Requests);
    }

    [Fact]
    public async Task TheUnkeyedClientIsPickedWhenNoKeyIsSet()
    {
        var unkeyed = FakeChatClient.Replying("""{"worked":true,"confidence":0.1}""");
        var keyed = FakeChatClient.Replying(Verdict);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(unkeyed);
        services.AddKeyedSingleton<IChatClient>("judge", keyed);
        services.AddMnemosyneChatEvaluator();
        using var provider = services.BuildServiceProvider();

        var verdict = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0.1, verdict.Value.Score);
        Assert.Empty(keyed.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMissingClientThrowsOnResolve(bool keyed)
    {
        var services = new ServiceCollection();
        services.AddMnemosyneChatEvaluator(new ChatEvaluatorOptions { ChatClientKey = keyed ? "judge" : null });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IReasonletEvaluator>());
    }

    [Fact]
    public void InvalidOptionsThrowAtRegistration()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMnemosyneChatEvaluator(new ChatEvaluatorOptions { EvaluatorName = " " }));

        Assert.Equal("options", error.ParamName);
    }

    [Fact]
    public void ANullServiceCollectionThrows() =>
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddMnemosyneChatEvaluator(null!));
}
