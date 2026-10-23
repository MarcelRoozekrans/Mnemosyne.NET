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

    private const string Sentinel = "MINOS-MESSAGE-SENTINEL-7d3f";

    [Fact]
    public async Task ARateLimitedJudgeLeavesTheAgentsVerdictAndLeaksNoMessage()
    {
        using var provider = Provider(FakeDecisionClient.Failing(DecisionErrorKind.RateLimited, Sentinel));
        using var capture = new TelemetryCapture();

        var receipt = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(ReasonletVerdictSource.Agent, receipt.Value.Source);
        Assert.False(receipt.Value.Success);
        Assert.Null(_store.Executed!.Value.Evaluation);
        Assert.DoesNotContain(Sentinel, receipt.Value.ToString(), StringComparison.Ordinal);
        var evaluate = Assert.Single(capture.Activities, a => a.OperationName == "mnemosyne.evaluate");
        Assert.Equal(ActivityStatusCode.Error, evaluate.Status);
        Assert.Equal("Unavailable", evaluate.GetTagItem("error.type"));
        AssertNoSentinel(capture);
    }

    [Fact]
    public async Task TheEvaluatorsErrorNamesOnlyTheKind()
    {
        using var provider = Provider(FakeDecisionClient.Failing(DecisionErrorKind.RateLimited, Sentinel));

        var result = await provider.GetRequiredService<IReasonletEvaluator>().EvaluateAsync(MinosTestData.Request(), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal("minos: RateLimited", result.Error.Detail);
        Assert.DoesNotContain(Sentinel, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidQuestionsAreABugThatThrowsWithoutTheMessage()
    {
        using var provider = Provider(FakeDecisionClient.Failing(DecisionErrorKind.InvalidQuestions, Sentinel));
        using var capture = new TelemetryCapture();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct));

        Assert.DoesNotContain(Sentinel, thrown.ToString(), StringComparison.Ordinal);
        AssertNoSentinel(capture);
    }

    /// <summary>Asserts that no tag, status description or event of any captured activity holds the sentinel.</summary>
    private static void AssertNoSentinel(TelemetryCapture capture)
    {
        foreach (var activity in capture.Activities)
        {
            Assert.DoesNotContain(Sentinel, activity.StatusDescription ?? string.Empty, StringComparison.Ordinal);
            foreach (var tag in activity.TagObjects)
            {
                Assert.DoesNotContain(Sentinel, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }

            foreach (var recorded in activity.Events)
            {
                Assert.DoesNotContain(Sentinel, recorded.Name, StringComparison.Ordinal);
                foreach (var tag in recorded.Tags)
                {
                    Assert.DoesNotContain(Sentinel, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
                }
            }
        }
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
