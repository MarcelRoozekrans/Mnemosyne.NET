using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>Where <c>UseReasonlets</c> sits relative to a real <c>UseFunctionInvocation</c>, and what each order captures.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class PipelineOrderTests
{
    private static readonly ReasonletScope Scope = new("support");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OutsideFunctionInvocationCapturesTheToolShape()
    {
        var (client, store, queue) = Build(outside: true, expose: false, [Call("c1", "orders.get")]);
        await using var _ = queue;
        using var runs = new RunCounter();

        var response = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], Options(), Ct);
        await queue.DrainAsync(Ct);

        Assert.Equal("done", response.Text);
        Assert.Equal(["new_reasonlet"], runs.Seen);
        var capture = Assert.Single(store.Captures);
        Assert.Equal("orders.get", Assert.Single(ReasonletPromptFormatter.Steps(capture.Graph)).Label);
    }

    [Fact]
    public async Task InsideFunctionInvocationCapturesOnceWithoutTheParent()
    {
        var match = CaptureTestData.Match();
        var (client, store, queue) = Build(
            outside: false, expose: false, [Call("c1", "orders.get")],
            memory => memory.Returns(match));
        await using var _ = queue;
        using var runs = new RunCounter();

        await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], Options(), Ct);
        await queue.DrainAsync(Ct);

        // The trip that asks for the tool ends on an unanswered call, so it is skipped. The trip after it is a continuation:
        // it is captured on its own, without the parent that the first trip's recall injected.
        Assert.Equal(["skipped/pending_tool_calls", "new_reasonlet"], runs.Seen);
        var capture = Assert.Single(store.Captures);
        Assert.Equal("orders.get", Assert.Single(ReasonletPromptFormatter.Steps(capture.Graph)).Label);
        Assert.Null(capture.ParentId);
    }

    [Fact]
    public async Task FindProcedureRunsThroughFunctionInvocation()
    {
        var match = CaptureTestData.Match();
        var (client, store, queue) = Build(
            outside: true, expose: true, [Call("l1", ReasonletTools.FindProcedureName, new() { ["question"] = "q" }), Call("c1", "orders.get")],
            memory => memory.RecallHandler = (query, _) => Task.FromResult(
                Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(query.Top == 1 ? [] : [match])));
        await using var _ = queue;

        await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], Options(), Ct);
        await queue.DrainAsync(Ct);

        var capture = Assert.Single(store.Captures);
        Assert.Equal("orders.get", Assert.Single(ReasonletPromptFormatter.Steps(capture.Graph)).Label);
        Assert.Equal(match.Reasonlet.Id, capture.ParentId);
    }

    private static ChatOptions Options() => new ChatOptions
    {
        Tools = [AIFunctionFactory.Create((int id) => "found", "orders.get")],
    }.WithReasonletScope(Scope);

    private static FunctionCallContent Call(string id, string name, Dictionary<string, object?>? args = null) =>
        new(id, name, args ?? new Dictionary<string, object?> { ["id"] = 7 });

    /// <summary>Builds the pipeline; the scripted model answers each call in turn with one tool call, then with "done".</summary>
    private static (IChatClient Client, FakeReasonletMemory Store, ReasonletCaptureQueue Queue) Build(
        bool outside, bool expose, FunctionCallContent[] script, Action<FakeReasonletMemory>? configure = null)
    {
        var store = CaptureTestData.Memory();
        configure?.Invoke(store);
        var turn = 0;
        var model = FakeChatClient.Replying((_, _, _) =>
        {
            var at = Interlocked.Increment(ref turn) - 1;
            return Task.FromResult(at < script.Length
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [script[at]]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        });
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(store);
        services.AddReasonletCapture();
        var provider = services.BuildServiceProvider();
        var options = new ReasonletChatOptions { ExposeFindProcedure = expose };
        var builder = new ChatClientBuilder(model);
        _ = outside ? builder.UseReasonlets(options).UseFunctionInvocation() : builder.UseFunctionInvocation().UseReasonlets(options);
        return (builder.Build(provider), store, provider.GetRequiredService<ReasonletCaptureQueue>());
    }
}
