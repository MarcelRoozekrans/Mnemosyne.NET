using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using Result = ZeroAlloc.Results.Result<Mnemosyne.NET.Abstractions.ReasonletReceipt, Mnemosyne.NET.Abstractions.MnemosyneError>;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>Turning one finished chat call into a capture.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletCaptureProcessorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReasonletCaptureProcessor Processor(FakeReasonletMemory memory, string source = "test.source") =>
        new(memory, new ReasonletCaptureOptions { Source = source });

    [Fact]
    public async Task NewRunWithoutParentIsRememberedWithoutParent()
    {
        var memory = CaptureTestData.Memory();
        var item = CaptureTestData.Item();

        await Processor(memory).ProcessAsync(item, Ct);

        var capture = Assert.Single(memory.Captures);
        Assert.Equal(CaptureTestData.Question, capture.Question);
        Assert.Equal(2, capture.Graph.Nodes.Count);
        Assert.Null(capture.ParentId);
        Assert.Equal(new ReasonletScope("scope"), capture.Scope);
        Assert.Equal("test.source", capture.Provenance.Source);
        Assert.Equal("resp-1", capture.Provenance.RunId);
        Assert.Equal("subject-1", capture.Provenance.SubjectId);
        Assert.Equal(item.Duration, capture.Outcome.Duration);
        Assert.True(capture.Outcome.Success);
        Assert.Equal("Refunded.", capture.Answer);
    }

    [Fact]
    public async Task InjectedParentWins()
    {
        var memory = CaptureTestData.Memory();
        var injected = ReasonletId.New();
        var equal = CaptureTestData.Match(CaptureTestData.GraphOf(CaptureTestData.Refund()));
        var item = CaptureTestData.Item() with { InjectedParent = injected, Lookups = [[equal]] };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Equal(injected, Assert.Single(memory.Captures).ParentId);
    }

    [Fact]
    public async Task HashEqualLookupIsTheParent()
    {
        var memory = CaptureTestData.Memory();
        var first = CaptureTestData.Match();
        var other = CaptureTestData.Match();
        var equal = CaptureTestData.Match(CaptureTestData.GraphOf(CaptureTestData.Refund()));
        var item = CaptureTestData.Item() with { Lookups = [[first], [other, equal]] };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Equal(equal.Reasonlet.Id, Assert.Single(memory.Captures).ParentId);
    }

    [Fact]
    public async Task AMatchWithAnInvalidGraphIsNeverHashEqual()
    {
        var memory = CaptureTestData.Memory();
        var broken = CaptureTestData.Match(new ReasonletGraph { Nodes = [], Edges = [], SchemaVersion = ReasonletGraph.CurrentSchemaVersion });
        var first = CaptureTestData.Match();
        var item = CaptureTestData.Item() with { Lookups = [[first, broken]] };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Equal(first.Reasonlet.Id, Assert.Single(memory.Captures).ParentId);
    }

    [Fact]
    public async Task ARunGraphThatCannotBeHashedFallsBackToTheTopMatch()
    {
        var memory = CaptureTestData.Memory();
        var messages = new List<ChatMessage> { new(ChatRole.User, CaptureTestData.Question) };
        for (var i = 0; i <= ReasonletGraphLimits.MaxNodes; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{i}", $"tool.{i}")]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", "ok")]));
        }

        var top = CaptureTestData.Match();
        var item = CaptureTestData.Item(messages) with { Lookups = [[top]] };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Equal(top.Reasonlet.Id, Assert.Single(memory.Captures).ParentId);
    }

    [Fact]
    public async Task TopOfFirstNonEmptyLookupIsTheParentOtherwise()
    {
        var memory = CaptureTestData.Memory();
        var m1 = CaptureTestData.Match();
        var m2 = CaptureTestData.Match();
        var item = CaptureTestData.Item() with { Lookups = [[], [m1, m2]] };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Equal(m1.Reasonlet.Id, Assert.Single(memory.Captures).ParentId);
    }

    [Fact]
    public async Task EmptyLookupsGiveNoParent()
    {
        var memory = CaptureTestData.Memory();
        var item = CaptureTestData.Item() with { Lookups = [[], []] };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Null(Assert.Single(memory.Captures).ParentId);
    }

    [Fact]
    public async Task FindProcedureCallsAreStripped()
    {
        var memory = CaptureTestData.Memory();

        await Processor(memory).ProcessAsync(CaptureTestData.Item(CaptureTestData.RefundWithLookup()), Ct);
        await Processor(memory).ProcessAsync(CaptureTestData.Item(), Ct);

        Assert.Equal(2, memory.Captures.Count);
        Assert.Equal(memory.Captures[1].Graph.ComputeContentHash().Value, memory.Captures[0].Graph.ComputeContentHash().Value);
        Assert.Equal(2, memory.Captures[0].Graph.Nodes.Count);
    }

    [Fact]
    public async Task OnlyLookupGivesNoToolCallsSkip()
    {
        var memory = CaptureTestData.Memory();
        ChatMessage[] messages = [.. CaptureTestData.RefundWithLookup().Take(3), new(ChatRole.Assistant, "Nothing to do.")];

        await Processor(memory).ProcessAsync(CaptureTestData.Item(messages), Ct);

        Assert.Empty(memory.Captures);
    }

    [Fact]
    public async Task ATrailingUnansweredCallIsNotCaptured()
    {
        var memory = CaptureTestData.Memory();

        await Processor(memory).ProcessAsync(CaptureTestData.Item([.. CaptureTestData.Refund().Take(4)]), Ct);

        Assert.Empty(memory.Captures);
    }

    [Fact]
    public async Task ATrailingCallWithItsResultIsCaptured()
    {
        var memory = CaptureTestData.Memory();

        await Processor(memory).ProcessAsync(CaptureTestData.Item([.. CaptureTestData.Refund().Take(5)]), Ct);

        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task AFinalTextAfterAFullExchangeIsCaptured()
    {
        var memory = CaptureTestData.Memory();

        await Processor(memory).ProcessAsync(CaptureTestData.Item(), Ct);

        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task AStrippedLookupCallDoesNotCountAsPending()
    {
        var memory = CaptureTestData.Memory();
        ChatMessage[] messages = [.. CaptureTestData.Refund().Take(5), CaptureTestData.RefundWithLookup()[1]];

        await Processor(memory).ProcessAsync(CaptureTestData.Item(messages), Ct);

        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task NoQuestionIsSkipped()
    {
        var memory = CaptureTestData.Memory();

        await Processor(memory).ProcessAsync(CaptureTestData.Item([new ChatMessage(ChatRole.Assistant, "hi")]), Ct);

        Assert.Empty(memory.Captures);
    }

    [Fact]
    public async Task AMixedMessageKeepsItsOtherContent()
    {
        var memory = CaptureTestData.Memory();
        var item = CaptureTestData.Item(
        [
            new(ChatRole.User, CaptureTestData.Question),
            new(ChatRole.Assistant, [new TextContent("looking"), new FunctionCallContent("l1", ReasonletTools.FindProcedureName), new FunctionCallContent("c1", "orders.get")]),
            new(ChatRole.Tool, [new FunctionResultContent("l1", "[]"), new FunctionResultContent("c1", "found")]),
            new(ChatRole.Assistant, "Done."),
        ]);

        await Processor(memory).ProcessAsync(item, Ct);

        var graph = Assert.Single(memory.Captures).Graph;
        Assert.Equal("orders.get", Assert.Single(graph.Nodes).Tool);
    }

    [Theory]
    [InlineData("stop", true)]
    [InlineData("length", false)]
    [InlineData("content_filter", false)]
    [InlineData("tool_calls", true)]
    [InlineData(null, true)]
    public async Task OutcomeFollowsFinishReason(string? reason, bool success)
    {
        var memory = CaptureTestData.Memory();
        var item = CaptureTestData.Item() with { FinishReason = reason is null ? null : new ChatFinishReason(reason) };

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Equal(success, Assert.Single(memory.Captures).Outcome.Success);
    }

    [Fact]
    public async Task BlankFinalTextIsNotASuccess()
    {
        var memory = CaptureTestData.Memory();
        ChatMessage[] messages = [.. CaptureTestData.Refund().Take(5), new(ChatRole.Assistant, "  ")];

        await Processor(memory).ProcessAsync(CaptureTestData.Item(messages), Ct);

        Assert.False(Assert.Single(memory.Captures).Outcome.Success);
    }

    [Fact]
    public async Task AFinalAssistantMessageWithoutTextIsNotASuccess()
    {
        var memory = CaptureTestData.Memory();
        ChatMessage[] messages = [.. CaptureTestData.Refund().Take(5)];

        await Processor(memory).ProcessAsync(CaptureTestData.Item(messages), Ct);

        Assert.False(Assert.Single(memory.Captures).Outcome.Success);
    }

    [Fact]
    public async Task RememberErrorIsCountedNotThrown()
    {
        var memory = new FakeReasonletMemory
        {
            RememberHandler = (_, _) => Task.FromResult(Result.Failure(new MnemosyneError(MnemosyneErrorCode.Blocked, "blocked"))),
        };

        await Processor(memory).ProcessAsync(CaptureTestData.Item(), Ct);

        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task RememberExceptionIsCountedNotThrown()
    {
        var memory = new FakeReasonletMemory();

        await Processor(memory).ProcessAsync(CaptureTestData.Item(), Ct);

        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task ACancelledCallerIsRethrown()
    {
        using var cts = new CancellationTokenSource();
        var memory = new FakeReasonletMemory
        {
            RememberHandler = async (_, ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return CaptureTestData.Receipt();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Processor(memory).ProcessAsync(CaptureTestData.Item(), cts.Token));
    }

    [Fact]
    public async Task AnOperationCanceledNotFromTheCallerIsSwallowed()
    {
        var memory = new FakeReasonletMemory { RememberHandler = (_, _) => throw new OperationCanceledException() };

        await Processor(memory).ProcessAsync(CaptureTestData.Item(), Ct);

        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task ItemMessagesAreNotMutated()
    {
        var memory = CaptureTestData.Memory();
        var messages = CaptureTestData.RefundWithLookup();
        var item = CaptureTestData.Item(messages);
        var before = messages.Select(m => (m, m.Contents.Count, m.Contents.ToArray())).ToArray();

        await Processor(memory).ProcessAsync(item, Ct);

        Assert.Same(messages, item.Messages);
        Assert.Equal(8, messages.Length);
        foreach (var (message, count, contents) in before)
        {
            Assert.Equal(count, message.Contents.Count);
            Assert.Equal(contents, message.Contents);
        }
    }
}
