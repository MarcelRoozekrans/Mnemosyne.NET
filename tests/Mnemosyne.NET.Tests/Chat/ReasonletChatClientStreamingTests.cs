using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>What the chat middleware does on a streamed call: the same preparation, and a capture only once the stream is read to the end.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletChatClientStreamingTests : IAsyncDisposable
{
    private const string Question = CaptureTestData.Question;

    private static readonly ReasonletScope Default = new("default");

    private readonly FakeReasonletMemory _memory = new();
    private readonly FakeReasonletMemory _store = CaptureTestData.Memory();
    private readonly ReasonletCaptureQueue _queue;

    public ReasonletChatClientStreamingTests() =>
        _queue = new ReasonletCaptureQueue(new ReasonletCaptureProcessor(_store, new ReasonletCaptureOptions()), new ReasonletCaptureOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _queue.DisposeAsync();

    [Fact]
    public async Task StreamedUpdatesPassThroughUnchanged()
    {
        var updates = Updates();
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(updates));

        var seen = await ReadAsync(Client(inner).GetStreamingResponseAsync([new(ChatRole.User, Question)], null, Ct));

        Assert.Equal(updates.Count, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.Same(updates[i], seen[i]);
        }
    }

    [Fact]
    public async Task FullyReadStreamIsCaptured()
    {
        var updates = Updates();
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(updates));
        var step = TimeSpan.FromMilliseconds(250);

        await ReadAsync(Client(inner, new SteppingTimeProvider(step)).GetStreamingResponseAsync([new(ChatRole.User, Question)], null, Ct));

        var capture = Assert.Single(await CapturedAsync());
        Assert.Equal(Question, capture.Question);
        Assert.Equal("Refunded.", capture.Answer);
        Assert.Equal(step, capture.Outcome.Duration);
        var expected = CaptureTestData.GraphOf([new(ChatRole.User, Question), .. updates.ToChatResponse().Messages]);
        Assert.Equal(expected.ComputeContentHash().Value, capture.Graph.ComputeContentHash().Value);
    }

    [Fact]
    public async Task AbandonedStreamIsNotCaptured()
    {
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(Updates()));

        await foreach (var _ in Client(inner).GetStreamingResponseAsync([new(ChatRole.User, Question)], null, Ct))
        {
            break;
        }

        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task ThrowingStreamIsNotCaptured()
    {
        var inner = FakeChatClient.Streaming((_, _, _) => Throw(Updates()));

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await ReadAsync(Client(inner).GetStreamingResponseAsync([new(ChatRole.User, Question)], null, Ct)));

        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task CancelledStreamIsNotCaptured()
    {
        using var cts = new CancellationTokenSource();
        var inner = FakeChatClient.Streaming((_, _, ct) => Cancel(Updates(), cts, ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ReadAsync(Client(inner).GetStreamingResponseAsync([new(ChatRole.User, Question)], null, cts.Token)));

        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task ATokenOnTheEnumeratorAndOneOnTheCallAreBothHonoured()
    {
        using var callCts = new CancellationTokenSource();
        using var enumeratorCts = new CancellationTokenSource();
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(Updates()));
        var stream = Client(inner).GetStreamingResponseAsync([new(ChatRole.User, Question)], null, callCts.Token).WithCancellation(enumeratorCts.Token);

        var count = 0;
        await foreach (var _ in stream)
        {
            count++;
        }

        Assert.Equal(Updates().Count, count);
        Assert.Single(await CapturedAsync());
    }

    [Fact]
    public async Task StreamingInjectsLikeNonStreaming()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(Updates()));
        ChatMessage q = new(ChatRole.User, Question);

        await ReadAsync(Client(inner).GetStreamingResponseAsync([q], null, Ct));

        var sent = Assert.Single(inner.Requests);
        Assert.Equal(2, sent.Count);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal(ReasonletPromptFormatter.Format(match), sent[0].Text);
        Assert.Same(q, sent[1]);
        var capture = Assert.Single(await CapturedAsync());
        Assert.Equal(match.Reasonlet.Id, capture.ParentId);
    }

    [Fact]
    public async Task ScopeKeyNeverReachesTheInnerClient()
    {
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(Updates()));
        var options = new ChatOptions { Temperature = 0.5f }.WithReasonletScope(Default, "s");

        await ReadAsync(Client(inner, null, new ReasonletChatOptions()).GetStreamingResponseAsync([new(ChatRole.User, Question)], options, Ct));

        var sent = Assert.Single(inner.Options);
        Assert.NotNull(sent);
        Assert.Equal(0.5f, sent.Temperature);
        Assert.False(sent.AdditionalProperties?.ContainsKey(ReasonletChatOptionsExtensions.ScopeKey) ?? false);
        Assert.True(options.AdditionalProperties!.ContainsKey(ReasonletChatOptionsExtensions.ScopeKey));
    }

    [Fact]
    public async Task NoScopePassesThroughTheCallersInstances()
    {
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(Updates()));
        var options = new ChatOptions();
        List<ChatMessage> messages = [new(ChatRole.User, Question)];

        await ReadAsync(Client(inner, null, new ReasonletChatOptions()).GetStreamingResponseAsync(messages, options, Ct));

        Assert.Same(messages, Assert.Single(inner.Received));
        Assert.Same(options, Assert.Single(inner.Options));
        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task ABypassedStreamPassesThroughWithoutTheKey()
    {
        _memory.Returns(CaptureTestData.Match());
        var updates = Updates();
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(updates));
        var options = new ChatOptions { AdditionalProperties = new() { [ReasonletChatClient.BypassKey] = true } };
        List<ChatMessage> messages = [new(ChatRole.User, Question)];

        var seen = await ReadAsync(Client(inner).GetStreamingResponseAsync(messages, options, Ct));

        Assert.Equal(updates.Count, seen.Count);
        Assert.Same(messages, Assert.Single(inner.Received));
        Assert.False(Assert.Single(inner.Options)!.AdditionalProperties!.ContainsKey(ReasonletChatClient.BypassKey));
        Assert.True(options.AdditionalProperties.ContainsKey(ReasonletChatClient.BypassKey));
        Assert.Empty(_memory.Queries);
        Assert.Empty(await CapturedAsync());
    }

    [Fact]
    public async Task AppendingToTheCallersListMidStreamDoesNotChangeTheCapture()
    {
        var inner = FakeChatClient.Streaming((_, _, _) => Yield(Updates()));
        List<ChatMessage> messages = [new(ChatRole.User, Question)];

        await foreach (var update in Client(inner).GetStreamingResponseAsync(messages, null, Ct))
        {
            messages.Add(new ChatMessage(update.Role ?? ChatRole.Assistant, update.Contents));
        }

        // The capture's question and steps are the request's, followed by the response's, each exactly once.
        var capture = Assert.Single(await CapturedAsync());
        var expected = CaptureTestData.GraphOf([new(ChatRole.User, Question), .. Updates().ToChatResponse().Messages]);
        Assert.Equal(expected.ComputeContentHash().Value, capture.Graph.ComputeContentHash().Value);
    }

    /// <summary>The refund's tool calls and answer, as streamed updates.</summary>
    private static List<ChatResponseUpdate> Updates() =>
    [
        .. CaptureTestData.Refund().Skip(1).Select(m => new ChatResponseUpdate(m.Role, m.Contents)),
    ];

    private static async IAsyncEnumerable<ChatResponseUpdate> Yield(IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            yield return update;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Throw(IEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var update in Yield(updates))
        {
            yield return update;
        }

        throw new HttpRequestException("down");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Cancel(IEnumerable<ChatResponseUpdate> updates, CancellationTokenSource cts, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in Yield(updates))
        {
            yield return update;
        }

        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<List<ChatResponseUpdate>> ReadAsync(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        List<ChatResponseUpdate> seen = [];
        await foreach (var update in stream)
        {
            seen.Add(update);
        }

        return seen;
    }

    private ReasonletChatClient Client(IChatClient inner, TimeProvider? time = null, ReasonletChatOptions? options = null) =>
        new(inner, _memory, _queue, options ?? new ReasonletChatOptions { DefaultScope = Default }, time ?? TimeProvider.System);

    private async Task<IReadOnlyList<ReasonletCapture>> CapturedAsync()
    {
        await _queue.DrainAsync(Ct);
        return _store.Captures;
    }
}
