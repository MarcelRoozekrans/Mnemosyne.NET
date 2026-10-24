using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>The facade with the chat evaluator registered, from the container down to the receipt.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, so its capture hears only its own trace.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ChatFacadeTests
{
    private const string Sentinel = "CHAT-REPLY-SENTINEL-4b1e";

    private readonly FakeStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Provider(IChatClient client)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        services.AddSingleton(client);
        services.AddMnemosyneChatEvaluator();
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

    [Fact]
    public async Task AJudgedRunCarriesTheJudgesVerdict()
    {
        using var provider = Provider(FakeChatClient.Replying("""{"worked":true,"confidence":0.9}"""));

        var receipt = await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(ReasonletVerdictSource.Evaluator, receipt.Value.Source);
        Assert.True(receipt.Value.Success);
        Assert.Equal(new ReasonletEvaluation(true, 0.9, "llm-judge"), _store.Executed!.Value.Evaluation);
    }

    [Fact]
    public async Task AnUnparseableReplyLeavesTheAgentsVerdictAndLeaksNoReplyText()
    {
        using var provider = Provider(FakeChatClient.Replying($"not json {Sentinel}"));
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
    public async Task TheEvaluateActivityNamesTheJudge()
    {
        using var provider = Provider(FakeChatClient.Replying("""{"worked":true,"confidence":0.9}"""));
        using var capture = new TelemetryCapture();

        await provider.GetRequiredService<IReasonletMemory>().RecordExecutionAsync(Run(), Ct);

        var activity = Assert.Single(capture.Activities, a => a.OperationName == "mnemosyne.evaluate");
        Assert.Equal("llm-judge", activity.GetTagItem("mnemosyne.evaluator"));
    }
}
