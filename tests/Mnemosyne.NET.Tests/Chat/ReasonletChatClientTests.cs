using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>What the chat middleware recalls, injects, exposes and hands to the capture queue on a non-streaming call.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, because it asserts the recall counter.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletChatClientTests : IAsyncDisposable
{
    private const string Recalls = "mnemosyne.chat.recalls";
    private const string Question = CaptureTestData.Question;

    private static readonly ReasonletScope Default = new("default");
    private static readonly ReasonletScope PerCall = new("per-call");

    // The recall side and the capture side are separate fakes, so a test can tell the two apart.
    private readonly FakeReasonletMemory _memory = new();
    private readonly FakeReasonletMemory _store = CaptureTestData.Memory();
    private readonly ReasonletCaptureQueue _queue;

    public ReasonletChatClientTests() =>
        _queue = new ReasonletCaptureQueue(new ReasonletCaptureProcessor(_store, new ReasonletCaptureOptions()), new ReasonletCaptureOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _queue.DisposeAsync();

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task NoScopePassesThrough(bool recall, bool capture)
    {
        var inner = Run();
        var options = new ChatOptions { Temperature = 0.5f };
        List<ChatMessage> messages = [new(ChatRole.User, Question)];
        using var telemetry = new TelemetryCapture();

        await Client(inner, new ReasonletChatOptions { Recall = recall, Capture = capture, ExposeFindProcedure = true }).GetResponseAsync(messages, options, Ct);

        Assert.Same(messages, Assert.Single(inner.Received));
        Assert.Same(options, Assert.Single(inner.Options));
        Assert.Empty(_memory.Queries);
        Assert.Empty(await CapturedAsync());
        if (recall || capture)
        {
            AssertRecall(telemetry, "skipped", "no_scope");
        }
        else
        {
            Assert.Empty(telemetry.Of(Recalls));
        }
    }

    [Fact]
    public async Task ABypassedCallPassesThroughWithoutTheKey()
    {
        _memory.Returns(CaptureTestData.Match());
        var inner = Run();
        var options = new ChatOptions
        {
            Temperature = 0.5f,
            AdditionalProperties = new() { [ReasonletChatClient.BypassKey] = true, ["kept"] = 1 },
        }.WithReasonletScope(PerCall);
        List<ChatMessage> messages = [new(ChatRole.User, Question)];
        using var telemetry = new TelemetryCapture();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, ExposeFindProcedure = true }).GetResponseAsync(messages, options, Ct);

        Assert.Same(messages, Assert.Single(inner.Received));
        var sent = Assert.Single(inner.Options)!;
        Assert.NotSame(options, sent);
        Assert.Equal(0.5f, sent.Temperature);
        Assert.Null(sent.Tools);
        Assert.False(sent.AdditionalProperties!.ContainsKey(ReasonletChatClient.BypassKey));
        Assert.True(options.AdditionalProperties!.ContainsKey(ReasonletChatClient.BypassKey));
        Assert.Empty(_memory.Queries);
        Assert.Empty(await CapturedAsync());
        Assert.Empty(telemetry.Of(Recalls));
        Assert.Empty(telemetry.Of("mnemosyne.capture.queue"));
    }

    [Fact]
    public async Task PerCallScopeWinsOverDefault()
    {
        var inner = Run();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default })
            .GetResponseAsync([new(ChatRole.User, Question)], new ChatOptions().WithReasonletScope(PerCall, "subject-9"), Ct);

        var query = Assert.Single(_memory.Queries);
        Assert.Equal(PerCall, query.Scope);
        var capture = Assert.Single(await CapturedAsync());
        Assert.Equal(PerCall, capture.Scope);
        Assert.Equal("subject-9", capture.Provenance.SubjectId);
    }

    [Fact]
    public async Task DefaultScopeAppliesWithoutOptions()
    {
        var inner = Run();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, MinSimilarity = 0.7 })
            .GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        var query = Assert.Single(_memory.Queries);
        Assert.Equal(Default, query.Scope);
        Assert.Equal(Question, query.Question);
        Assert.Equal(1, query.Top);
        Assert.Equal(0.7, query.MinSimilarity);
        Assert.Null(Assert.Single(inner.Options));
        var capture = Assert.Single(await CapturedAsync());
        Assert.Equal(Default, capture.Scope);
        Assert.Null(capture.Provenance.SubjectId);
    }

    [Fact]
    public async Task MatchIsInjectedAfterLeadingSystemMessages()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var inner = Run();
        ChatMessage a = new(ChatRole.System, "a");
        ChatMessage b = new(ChatRole.System, "b");
        ChatMessage q = new(ChatRole.User, Question);
        List<ChatMessage> messages = [a, b, q];
        using var telemetry = new TelemetryCapture();

        await Client(inner, Defaults()).GetResponseAsync(messages, null, Ct);

        var sent = Assert.Single(inner.Requests);
        Assert.Equal(4, sent.Count);
        Assert.Same(a, sent[0]);
        Assert.Same(b, sent[1]);
        Assert.Equal(ChatRole.System, sent[2].Role);
        Assert.Equal(ReasonletPromptFormatter.Format(match), sent[2].Text);
        Assert.Same(q, sent[3]);
        Assert.Equal([a, b, q], messages);
        AssertRecall(telemetry, "injected");
    }

    [Fact]
    public async Task ACustomFormatterWritesTheInject()
    {
        _memory.Returns(CaptureTestData.Match());
        var inner = Run();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, Formatter = m => $"use {m.Reasonlet.Name}" }).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        var sent = Assert.Single(inner.Requests);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal($"use {CaptureTestData.Match().Reasonlet.Name}", sent[0].Text);
    }

    [Fact]
    public async Task NoMatchInjectsNothing()
    {
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        await Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        AssertOnlyQuestionSent(inner);
        AssertRecall(telemetry, "no_match");
    }

    [Fact]
    public async Task RecallErrorInjectsNothing()
    {
        _memory.Fails(new MnemosyneError(MnemosyneErrorCode.Unavailable, "down"));
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        await Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        AssertOnlyQuestionSent(inner);
        AssertRecall(telemetry, "Unavailable");
        Assert.Single(await CapturedAsync());
    }

    [Fact]
    public async Task RecallExceptionInjectsNothing()
    {
        _memory.Throws(new HttpRequestException("refused"));
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        await Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        AssertOnlyQuestionSent(inner);
        Assert.Equal("exception", Assert.Single(telemetry.Of(Recalls)).Tag("mnemosyne.outcome"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancellationThatIsNeitherTheCallersNorTheTimeoutIsAnException(bool infinite)
    {
        _memory.Throws(new OperationCanceledException("own"));
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, RecallTimeout = infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(5) })
            .GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        AssertOnlyQuestionSent(inner);
        AssertRecall(telemetry, "exception");
    }

    [Fact]
    public async Task ALazySequenceIsReadOnce()
    {
        _memory.Returns(CaptureTestData.Match());
        var reads = 0;
        IEnumerable<ChatMessage> Lazy()
        {
            reads++;
            yield return new ChatMessage(ChatRole.User, Question);
        }

        var inner = Run();

        await Client(inner, Defaults()).GetResponseAsync(Lazy(), null, Ct);

        Assert.Equal(1, reads);
        Assert.Equal([ChatRole.System, ChatRole.User], Assert.Single(inner.Requests).Select(m => m.Role));
        Assert.Equal(Question, Assert.Single(await CapturedAsync()).Question);
    }

    [Fact]
    public async Task RecallTimeoutInjectsNothing()
    {
        // The recall ignores its token and never ends, so only the middleware's own timeout can end the wait.
        _memory.RecallHandler = (_, _) => new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        var clock = new ManualTimeProvider();
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        var call = Client(inner, new ReasonletChatOptions { DefaultScope = Default, RecallTimeout = TimeSpan.FromSeconds(2) }, clock)
            .GetResponseAsync([new(ChatRole.User, Question)], null, Ct);
        Assert.False(call.IsCompleted);
        clock.Fire();
        await call.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal([TimeSpan.FromSeconds(2)], clock.DueTimes.Take(1));
        AssertOnlyQuestionSent(inner);
        AssertRecall(telemetry, "timeout");
    }

    [Fact]
    public async Task AnInfiniteRecallTimeoutStartsNoTimer()
    {
        _memory.Returns(CaptureTestData.Match());
        var clock = new ManualTimeProvider();
        var inner = Run();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, RecallTimeout = Timeout.InfiniteTimeSpan }, clock)
            .GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        Assert.Empty(clock.DueTimes);
        Assert.Equal(2, Assert.Single(inner.Requests).Count);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        _memory.RecallHandler = async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, cts.Token));

        Assert.Empty(inner.Requests);
        Assert.Empty(telemetry.Of(Recalls));
        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task CallerCancellationIgnoredByTheRecallStillPropagates()
    {
        using var cts = new CancellationTokenSource();
        _memory.RecallHandler = (_, _) =>
        {
            cts.Cancel();
            return new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        };
        var inner = Run();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, cts.Token).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task CallerCancellationOfTheInnerCallPropagates()
    {
        using var cts = new CancellationTokenSource();
        var inner = FakeChatClient.Replying((_, _, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, cts.Token));

        Assert.Single(_memory.Queries);
        Assert.Empty(await CapturedAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FormatterFailureInjectsNothing(bool throws)
    {
        _memory.Returns(CaptureTestData.Match());
        var inner = Run();
        using var telemetry = new TelemetryCapture();
        Func<ReasonletMatch, string> formatter = throws ? _ => throw new FormatException("bad") : _ => "  ";

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, Formatter = formatter }).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        AssertOnlyQuestionSent(inner);
        AssertRecall(telemetry, "formatter_failed");
        Assert.Null(Assert.Single(await CapturedAsync()).ParentId);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("assistant")]
    [InlineData("blank")]
    [InlineData("empty")]
    public async Task ContinuationIsNotRecalledButIsCaptured(string last)
    {
        _memory.Returns(CaptureTestData.Match());
        var refund = CaptureTestData.Refund();
        List<ChatMessage> messages = last switch
        {
            "tool" => [.. refund.Take(5)],
            "assistant" => [.. refund.Take(4)],
            "blank" => [.. refund, new(ChatRole.User, " ")],
            _ => [],
        };
        var inner = Run([new ChatMessage(ChatRole.Assistant, "Refunded.")]);
        using var telemetry = new TelemetryCapture();
        using var runs = new RunCounter();

        await Client(inner, Defaults()).GetResponseAsync(messages, null, Ct);

        Assert.Empty(_memory.Queries);
        Assert.Equal(messages, Assert.Single(inner.Requests));
        AssertRecall(telemetry, "skipped", "continuation");
        var captured = await CapturedAsync();
        if (last == "empty")
        {
            // Nothing to capture without a question, but the call still went to the queue and on to the processor.
            Assert.Empty(captured);
            Assert.Equal(["skipped/no_question"], runs.Seen);
        }
        else
        {
            Assert.Equal(Question, Assert.Single(captured).Question);
            Assert.Equal(["new_reasonlet"], runs.Seen);
        }
    }

    [Fact]
    public async Task CallerOptionsAreNotMutated()
    {
        _memory.Returns(CaptureTestData.Match());
        var other = AIFunctionFactory.Create(() => "x", "other_tool");
        var options = new ChatOptions { Tools = [other] }.WithReasonletScope(PerCall);
        var tools = options.Tools;
        var properties = options.AdditionalProperties;
        List<ChatMessage> messages = [new(ChatRole.User, Question)];
        var inner = Run();
        var client = Client(inner, new ReasonletChatOptions { DefaultScope = Default, ExposeFindProcedure = true });

        await client.GetResponseAsync(messages, options, Ct);
        await client.GetResponseAsync(messages, options, Ct);

        Assert.Same(tools, options.Tools);
        Assert.Equal([other], options.Tools);
        Assert.Same(properties, options.AdditionalProperties);
        Assert.Equal([ReasonletChatOptionsExtensions.ScopeKey], options.AdditionalProperties!.Keys);
        Assert.Single(messages);
        Assert.All(inner.Options, sent => Assert.NotSame(options, sent));
        Assert.All(inner.Options, sent => Assert.Equal(["other_tool", ReasonletTools.FindProcedureName], sent!.Tools!.Select(t => t.Name)));
    }

    [Fact]
    public async Task ScopeKeyIsRemovedBeforeTheInnerCall()
    {
        var inner = Run();
        var options = new ChatOptions { AdditionalProperties = new() { ["kept"] = 1 } }.WithReasonletScope(PerCall);
        var bare = new ChatOptions().WithReasonletScope(PerCall);
        var client = Client(inner, Defaults());

        await client.GetResponseAsync([new(ChatRole.User, Question)], options, Ct);
        await client.GetResponseAsync([new(ChatRole.User, Question)], bare, Ct);

        Assert.Equal(["kept"], inner.Options[0]!.AdditionalProperties!.Keys);
        Assert.False(inner.Options[1]!.AdditionalProperties?.ContainsKey(ReasonletChatOptionsExtensions.ScopeKey) ?? false);
        Assert.Equal(2, _memory.Queries.Count);
    }

    [Fact]
    public async Task FindProcedureIsAddedOnceAndNotWhenPresent()
    {
        var inner = Run();
        var own = ReasonletTools.CreateFindProcedure(_memory, PerCall);
        var client = Client(inner, new ReasonletChatOptions { DefaultScope = Default, ExposeFindProcedure = true });

        await client.GetResponseAsync([new(ChatRole.User, Question)], null, Ct);
        await client.GetResponseAsync([new(ChatRole.User, Question)], new ChatOptions { Tools = [own] }, Ct);
        await Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        var added = Assert.Single(inner.Options[0]!.Tools!);
        Assert.IsType<FindProcedureFunction>(added);
        Assert.Same(own, Assert.Single(inner.Options[1]!.Tools!));
        Assert.Null(inner.Options[2]);
    }

    [Fact]
    public async Task CaptureItemCarriesParentLookupsAndMetadata()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var response = new ChatResponse([.. CaptureTestData.Refund().Skip(1)]) { ResponseId = "resp-42", FinishReason = ChatFinishReason.Length };
        var inner = Run(response);
        var step = TimeSpan.FromMilliseconds(1234);

        var returned = await Client(inner, Defaults(), new SteppingTimeProvider(step))
            .GetResponseAsync([new(ChatRole.System, "be brief"), new(ChatRole.User, Question)], null, Ct);

        Assert.Same(response, returned);
        var capture = Assert.Single(await CapturedAsync());
        Assert.Equal(match.Reasonlet.Id, capture.ParentId);
        Assert.Equal(step, capture.Outcome.Duration);
        Assert.Equal("resp-42", capture.Provenance.RunId);
        Assert.False(capture.Outcome.Success);
        Assert.Equal(Question, capture.Question);
        Assert.Equal("Refunded.", capture.Answer);
    }

    [Fact]
    public void CaptureItemLeavesTheInjectOut()
    {
        // The processor ignores system messages ahead of the question, so only the item itself shows the inject is left out.
        ChatMessage q = new(ChatRole.User, Question);
        ChatMessage inject = new(ChatRole.System, "suggestion");
        ChatMessage answer = new(ChatRole.Assistant, "done");
        var parent = ReasonletId.New();
        var lookup = new FindProcedureFunction(_memory, PerCall);
        var call = new ReasonletChatClient.PreparedCall(new ReasonletCallScope(PerCall, "s"), [q], [inject, q], null, parent, lookup);
        var response = new ChatResponse(answer) { ResponseId = "r", FinishReason = ChatFinishReason.Stop };

        var item = ReasonletChatClient.CaptureItem(call, response, TimeSpan.FromSeconds(3));

        Assert.Equal([q, answer], item.Messages);
        Assert.Equal(PerCall, item.Scope);
        Assert.Equal("s", item.SubjectId);
        Assert.Equal(parent, item.InjectedParent);
        Assert.Empty(item.Lookups);
        Assert.Equal(TimeSpan.FromSeconds(3), item.Duration);
        Assert.Equal("r", item.ResponseId);
        Assert.Equal(ChatFinishReason.Stop, item.FinishReason);
    }

    [Fact]
    public async Task LookupsReachTheCapture()
    {
        var looked = CaptureTestData.Match();
        _memory.RecallHandler = (query, _) => Task.FromResult(
            Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(query.Top == 1 ? [] : [looked]));
        var inner = FakeChatClient.Replying(async (_, options, ct) =>
        {
            var tool = (AIFunction)options!.Tools!.Single(t => t.Name == ReasonletTools.FindProcedureName);
            await tool.InvokeAsync(new AIFunctionArguments { ["question"] = "refunds" }, ct);
            return new ChatResponse([.. CaptureTestData.Refund().Skip(1)]);
        });

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, ExposeFindProcedure = true }).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        Assert.Equal(looked.Reasonlet.Id, Assert.Single(await CapturedAsync()).ParentId);
    }

    [Fact]
    public async Task InnerExceptionIsNotCaptured()
    {
        var inner = FakeChatClient.Throwing(new HttpRequestException("down"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, Ct));

        Assert.Single(_memory.Queries);
        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task AHandOffThatThrowsIsCountedAndTheCallGoesOn()
    {
        var response = new ChatResponse { Messages = new ThrowingList() };
        var inner = Run(response);
        using var telemetry = new TelemetryCapture();

        var returned = await Client(inner, Defaults()).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        Assert.Same(response, returned);
        Assert.Empty(await CapturedAsync());
        var count = Assert.Single(telemetry.Of("mnemosyne.capture.queue"));
        Assert.Equal("exception", count.Tag("mnemosyne.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, count.Tag("error.type"));
    }

    [Fact]
    public async Task CaptureOffEnqueuesNothing()
    {
        var inner = Run();

        // The queue is passed anyway, so the switch alone has to stop the hand-off.
        await new ReasonletChatClient(inner, _memory, _queue, new ReasonletChatOptions { DefaultScope = Default, Capture = false }, TimeProvider.System)
            .GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        Assert.Single(_memory.Queries);
        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task RecallOffDoesNotRecall()
    {
        _memory.Returns(CaptureTestData.Match());
        var inner = Run();
        using var telemetry = new TelemetryCapture();

        await Client(inner, new ReasonletChatOptions { DefaultScope = Default, Recall = false }).GetResponseAsync([new(ChatRole.User, Question)], null, Ct);

        Assert.Empty(_memory.Queries);
        AssertOnlyQuestionSent(inner);
        Assert.Empty(telemetry.Of(Recalls));
        Assert.Single(await CapturedAsync());
    }

    private static ReasonletChatOptions Defaults() => new() { DefaultScope = Default };

    /// <summary>An inner client that runs the refund's tool calls, so the processor has something to capture.</summary>
    private static FakeChatClient Run(IList<ChatMessage>? reply = null) =>
        Run(new ChatResponse(reply ?? [.. CaptureTestData.Refund().Skip(1)]));

    private static FakeChatClient Run(ChatResponse response) => FakeChatClient.Replying(response);

    private static void AssertOnlyQuestionSent(FakeChatClient inner)
    {
        var sent = Assert.Single(Assert.Single(inner.Requests));
        Assert.Equal(ChatRole.User, sent.Role);
        Assert.Equal(Question, sent.Text);
    }

    private static void AssertRecall(TelemetryCapture telemetry, string outcome, string? reason = null)
    {
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal(1, count.Value);
        Assert.Equal(outcome, count.Tag("mnemosyne.outcome"));
        Assert.Equal(reason, count.Tag("mnemosyne.skip_reason"));
    }

    private ReasonletChatClient Client(IChatClient inner, ReasonletChatOptions options, TimeProvider? time = null) =>
        new(inner, _memory, _queue, options, time ?? TimeProvider.System);

    private async Task<IReadOnlyList<ReasonletCapture>> CapturedAsync()
    {
        await _queue.DrainAsync(Ct);
        return _store.Captures;
    }
}

/// <summary>A message list whose every member throws, to force a failure where the hand-off reads the response.</summary>
internal sealed class ThrowingList : IList<ChatMessage>
{
    public int Count => throw new InvalidOperationException();

    public bool IsReadOnly => throw new InvalidOperationException();

    public ChatMessage this[int index]
    {
        get => throw new InvalidOperationException();
        set => throw new InvalidOperationException();
    }

    public int IndexOf(ChatMessage item) => throw new InvalidOperationException();

    public void Insert(int index, ChatMessage item) => throw new InvalidOperationException();

    public void RemoveAt(int index) => throw new InvalidOperationException();

    public void Add(ChatMessage item) => throw new InvalidOperationException();

    public void Clear() => throw new InvalidOperationException();

    public bool Contains(ChatMessage item) => throw new InvalidOperationException();

    public void CopyTo(ChatMessage[] array, int arrayIndex) => throw new InvalidOperationException();

    public bool Remove(ChatMessage item) => throw new InvalidOperationException();

    public IEnumerator<ChatMessage> GetEnumerator() => throw new InvalidOperationException();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A clock whose every timestamp is one <paramref name="step"/> after the previous, so any elapsed time is one step.</summary>
internal sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
{
    private long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Add(ref _now, step.Ticks);
}
