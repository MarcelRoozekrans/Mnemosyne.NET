using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>
/// The setup where the chat evaluator judges with the same unkeyed <see cref="IChatClient"/> that has
/// <c>UseReasonlets</c>: the memory needs the evaluator, the evaluator needs the client, and the client needs the memory.
/// </summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class CombinedSetupTests
{
    private const string Verdict = """{"worked":true,"confidence":0.9}""";

    private static readonly ReasonletScope Scope = new("support");

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResolvingTheClientAndRunningTheHostedServicesDoesNotHang()
    {
        var (services, _, _) = Services();
        await using var provider = services.BuildServiceProvider();

        await Task.Run(
            async () =>
            {
                var client = provider.GetRequiredService<IChatClient>();
                Assert.NotNull(client.GetService<ReasonletChatClient>());
                var hosted = provider.GetServices<IHostedService>().ToList();
                foreach (var service in hosted)
                {
                    await service.StartAsync(Ct);
                }

                foreach (var service in hosted)
                {
                    await service.StopAsync(Ct);
                }
            },
            Ct).WaitAsync(Guard, Ct);
    }

    [Fact]
    public async Task StartingTheHostedServicesBeforeResolvingTheClientDoesNotHang()
    {
        var (services, _, _) = Services();
        await using var provider = services.BuildServiceProvider();

        await Task.Run(
            async () =>
            {
                var hosted = provider.GetRequiredService<IEnumerable<IHostedService>>().ToList();
                foreach (var service in hosted)
                {
                    await service.StartAsync(Ct);
                }

                var client = provider.GetRequiredService<IChatClient>();
                Assert.NotNull(client.GetService<ReasonletChatClient>());
                foreach (var service in hosted)
                {
                    await service.StopAsync(Ct);
                }
            },
            Ct).WaitAsync(Guard, Ct);
    }

    [Fact]
    public async Task TheJudgeCallPassesThroughWithoutRecallToolOrKey()
    {
        var (services, store, model) = Services();
        await using var provider = services.BuildServiceProvider();
        var client = await Task.Run(provider.GetRequiredService<IChatClient>, Ct).WaitAsync(Guard, Ct);

        var response = await client
            .GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct)
            .WaitAsync(Guard, Ct);
        await provider.GetRequiredService<ReasonletCaptureQueue>().DrainAsync(Ct).WaitAsync(Guard, Ct);

        Assert.Equal("done", response.Text);
        Assert.NotNull(store.Remembered?.Evaluation);
        var judge = Assert.Single(model.Options, o => o?.ResponseFormat is not null);
        Assert.Null(judge!.Tools);
        Assert.False(judge.AdditionalProperties?.ContainsKey(ReasonletChatClient.BypassKey) ?? false);
        var request = model.Requests[model.Options.ToList().IndexOf(judge)];
        Assert.Equal([ChatRole.System, ChatRole.User], request.Select(m => m.Role));

        // One search for the user's question and one remember: the judge call recalled nothing.
        Assert.Equal(2, store.Calls);
    }

    /// <summary>
    /// The combined setup. The scripted model answers a judge request with a verdict, and a chat request first with a
    /// tool call, then with "done".
    /// </summary>
    private static (ServiceCollection Services, FakeStore Store, FakeChatClient Model) Services()
    {
        var store = new FakeStore();
        var turn = 0;
        var model = FakeChatClient.Replying((_, options, _) =>
        {
            if (options?.ResponseFormat is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Verdict)));
            }

            return Task.FromResult(Interlocked.Increment(ref turn) == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "orders.get", new Dictionary<string, object?> { ["id"] = 7 })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        });

        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
        services.AddMnemosyne();
        services.AddMnemosyneChatEvaluator();
        services.AddReasonletCapture();
        services.AddChatClient(model)
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope })
            .UseFunctionInvocation(configure: f => f.AdditionalTools = [AIFunctionFactory.Create((int id) => "found", "orders.get")]);
        return (services, store, model);
    }
}
