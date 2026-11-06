using Microsoft.Extensions.AI;
using Mnemosyne.Samples;
using Xunit;

namespace Mnemosyne.NET.Tests.Samples;

public sealed class ScriptedChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedChatClient TwoCalls(string answer = "Refunded.") => new(
        [
            new ScriptedToolCall("orders_get", new Dictionary<string, object?> { ["order_id"] = "A-1" }),
            new ScriptedToolCall("payments_refund", new Dictionary<string, object?> { ["amount"] = 12.5 }),
        ],
        answer);

    [Fact]
    public async Task ARequestWithoutToolResultsGetsTheToolCalls()
    {
        using var client = TwoCalls();

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Refund A-1")], cancellationToken: Ct);

        var message = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        var calls = message.Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(["orders_get", "payments_refund"], calls.Select(c => c.Name));
        Assert.Equal(["call-1", "call-2"], calls.Select(c => c.CallId));
        Assert.Equal("A-1", calls[0].Arguments!["order_id"]);
        Assert.Equal(12.5, calls[1].Arguments!["amount"]);
    }

    [Fact]
    public async Task ARequestEndingInAToolResultGetsTheAnswer()
    {
        using var client = TwoCalls("All done.");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Refund A-1"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]),
        };

        var response = await client.GetResponseAsync(messages, cancellationToken: Ct);

        Assert.Equal("All done.", response.Text);
        Assert.DoesNotContain(response.Messages.SelectMany(m => m.Contents), c => c is FunctionCallContent);
    }

    [Fact]
    public async Task NoScriptedCallsAnswersAtOnce()
    {
        using var client = new ScriptedChatClient([], "Straight answer.");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: Ct);

        Assert.Equal("Straight answer.", response.Text);
        Assert.DoesNotContain(response.Messages.SelectMany(m => m.Contents), c => c is FunctionCallContent);
    }

    [Fact]
    public async Task RequestsAreCopiedWhenTheCallIsMade()
    {
        using var client = TwoCalls();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Refund A-1") };

        await client.GetResponseAsync(messages, cancellationToken: Ct);
        messages.Add(new ChatMessage(ChatRole.User, "later"));

        var request = Assert.Single(client.Requests);
        Assert.Equal("Refund A-1", Assert.Single(request).Text);
        Assert.Single(client.Options);
    }

    [Fact]
    public async Task OptionsAreClonedWhenTheCallIsMade()
    {
        using var client = TwoCalls();
        var options = new ChatOptions { Instructions = "B" };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], options, Ct);
        options.Instructions = "changed";

        Assert.Equal("B", client.SystemText(0));
    }

    [Fact]
    public async Task SystemTextJoinsSystemMessagesAndInstructions()
    {
        using var client = TwoCalls();

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "A"), new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions { Instructions = "B" },
            Ct);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: Ct);

        Assert.Equal("A\nB", client.SystemText(0));
        Assert.Equal("", client.SystemText(1));
    }

    [Fact]
    public async Task StreamingReplaysTheSameReply()
    {
        using var client = TwoCalls();
        var messages = new[] { new ChatMessage(ChatRole.User, "Refund A-1") };
        var expected = await client.GetResponseAsync(messages, cancellationToken: Ct);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: Ct))
        {
            updates.Add(update);
        }

        var streamed = updates.ToChatResponse();
        Assert.Equal(expected.Text, streamed.Text);
        Assert.Equal(
            expected.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => (c.CallId, c.Name)),
            streamed.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => (c.CallId, c.Name)));
    }

    [Fact]
    public async Task ACancelledTokenThrows()
    {
        using var client = TwoCalls();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task StreamingAnAnswerReplaysTheAnswer()
    {
        using var client = TwoCalls("All done.");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Refund A-1"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]),
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: Ct))
        {
            updates.Add(update);
        }

        var streamed = updates.ToChatResponse();
        Assert.Equal("All done.", streamed.Text);
        Assert.DoesNotContain(streamed.Messages.SelectMany(m => m.Contents), c => c is FunctionCallContent);
    }

    [Fact]
    public async Task ACancelledTokenThrowsWhileStreaming()
    {
        using var client = TwoCalls();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: cts.Token))
            {
            }
        });
    }
}
