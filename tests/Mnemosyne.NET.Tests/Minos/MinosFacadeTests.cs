using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Minos;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Minos;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Minos;

/// <summary>The facade with the Minos evaluator registered, from the container down to the receipt.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, so its capture hears only its own trace.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class MinosFacadeTests
{
    private readonly FakeStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Provider(IDecisionClient client)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        services.AddSingleton(client);
        services.AddMnemosyneMinosEvaluator();
        services.AddMnemosyne();
        return services.BuildServiceProvider();
    }

    private static ReasonletRun Run() => new()
    {
        Scope = new ReasonletScope("tenant"),
        Id = ReasonletId.New(),
        // The agent says the run failed, so a success can only come from the judge.
        Outcome = new ReasonletOutcome(false, TimeSpan.FromSeconds(2), null),
        Provenance = TestData.Provenance(),
        Question = "Where is invoice 42?",
        Answer = "It is in the March folder.",
    };

    [Theory]
    [InlineData(0.9, true)]
    [InlineData(0.1, false)]
    public async Task AJudgedRunCarriesTheJudgesVerdict(double probability, bool success)
    {
        using var provider = Provider(FakeDecisionClient.Answering(probability));

        var receipt = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(ReasonletVerdictSource.Evaluator, receipt.Value.Source);
        Assert.Equal(success, receipt.Value.Success);
        Assert.Equal(new ReasonletEvaluation(success, probability, "minos"), _store.Executed!.Value.Evaluation);
    }

    [Fact]
    public async Task ARateLimitedJudgeLeavesTheAgentsVerdict()
    {
        using var provider = Provider(FakeDecisionClient.Failing(DecisionErrorKind.RateLimited));

        var receipt = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(ReasonletVerdictSource.Agent, receipt.Value.Source);
        Assert.False(receipt.Value.Success);
        Assert.Null(_store.Executed!.Value.Evaluation);
    }

    [Fact]
    public async Task InvalidQuestionsAreABugThatThrows()
    {
        using var provider = Provider(FakeDecisionClient.Failing(DecisionErrorKind.InvalidQuestions));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct));
    }

    [Fact]
    public async Task TheEvaluateActivityNamesMinos()
    {
        using var provider = Provider(FakeDecisionClient.Answering(0.9));
        using var capture = new TelemetryCapture();

        await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct);

        var activity = Assert.Single(capture.Activities, a => a.OperationName == "mnemosyne.evaluate");
        Assert.Equal("minos", activity.GetTagItem("mnemosyne.evaluator"));
    }
}
