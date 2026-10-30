using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;
using static Mnemosyne.NET.Tests.Core.ReasonletTranscriptGraphTests;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The capture <see cref="ReasonletTranscript.ToCapture"/> builds: question, answer, graph and the caller's details.</summary>
public sealed class ReasonletTranscriptCaptureTests
{
    private static readonly TranscriptCaptureDetails Details = new()
    {
        Scope = new ReasonletScope("tenant"),
        Provenance = new ReasonletProvenance { Source = "chat", RunId = "run-1" },
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(2), 0.5),
        ParentId = ReasonletId.New(),
        Intent = "refund",
        Name = "Refund an order",
    };

    [Fact]
    public void TheCaptureCarriesTheQuestionAnswerGraphAndDetails()
    {
        ChatMessage[] messages =
        [
            User("Refund order 7"),
            Calls(("c1", "payments.refund", ["orderId"])), Results("c1"),
            new ChatMessage(ChatRole.Assistant, "Refunded."),
        ];

        var capture = Capture(messages);

        Assert.Equal("Refund order 7", capture.Question);
        Assert.Equal("Refunded.", capture.Answer);
        Assert.Equal(Hash(ReasonletTranscript.ToGraph(messages).Value), Hash(capture.Graph));
        Assert.Equal(Details.Scope, capture.Scope);
        Assert.Equal(Details.Provenance, capture.Provenance);
        Assert.Equal(Details.Outcome, capture.Outcome);
        Assert.Equal(Details.ParentId, capture.ParentId);
        Assert.Equal(Details.Intent, capture.Intent);
        Assert.Equal(Details.Name, capture.Name);
        Assert.True(capture.Validate().IsSuccess);
    }

    [Fact]
    public void OptionalDetailsStayNull()
    {
        var details = new TranscriptCaptureDetails { Scope = Details.Scope, Provenance = Details.Provenance, Outcome = Details.Outcome };

        var capture = ReasonletTranscript.ToCapture([User("Q"), Calls(("c1", "A", [])), Results("c1")], details).Value;

        Assert.Null(capture.ParentId);
        Assert.Null(capture.Intent);
        Assert.Null(capture.Name);
    }

    [Fact]
    public void TheAnswerIsTheLastNonBlankAssistantTextOfTheRun()
    {
        var capture = Capture(
            User("Q"),
            new ChatMessage(ChatRole.Assistant, "thinking"),
            Calls(("c1", "A", [])), Results("c1"),
            new ChatMessage(ChatRole.Assistant, "done"),
            new ChatMessage(ChatRole.Assistant, "  "));

        Assert.Equal("done", capture.Answer);
    }

    [Fact]
    public void WithoutAssistantTextTheAnswerIsNull()
    {
        Assert.Null(Capture(User("Q"), Calls(("c1", "A", [])), Results("c1")).Answer);
    }

    [Fact]
    public void AssistantTextBeforeTheQuestionIsNotTheAnswer()
    {
        var capture = Capture(new ChatMessage(ChatRole.Assistant, "old"), User("Q"), Calls(("c1", "A", [])), Results("c1"));

        Assert.Null(capture.Answer);
    }

    [Fact]
    public void AUserMessageWithoutTextIsNotTheQuestion()
    {
        var capture = Capture(
            User("Q"),
            Calls(("c1", "A", [])), Results("c1"),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]));

        Assert.Equal("Q", capture.Question);
        Assert.Equal("A", Assert.Single(capture.Graph.Nodes).Tool);
    }

    [Fact]
    public void AnAssistantMessageWithTextAndCallsGivesBoth()
    {
        var capture = Capture(
            User("Q"),
            new ChatMessage(ChatRole.Assistant, [new TextContent("on it"), new FunctionCallContent("c1", "A")]),
            Results("c1"));

        Assert.Equal("A", Assert.Single(capture.Graph.Nodes).Tool);
        Assert.Equal("on it", capture.Answer);
    }

    [Fact]
    public void SkipsPassThrough()
    {
        Assert.Equal(TranscriptSkipReason.NoQuestion, ReasonletTranscript.ToCapture([], Details).Error);
        Assert.Equal(TranscriptSkipReason.NoToolCalls, ReasonletTranscript.ToCapture([User("Q")], Details).Error);
    }

    [Fact]
    public void TheAnswerStaysRedactedInToString()
    {
        var capture = Capture(User("Q"), Calls(("c1", "A", [])), Results("c1"), new ChatMessage(ChatRole.Assistant, "secret answer"));

        Assert.DoesNotContain("secret answer", capture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => ReasonletTranscript.ToCapture([User("Q")], null!));
        Assert.Throws<ArgumentNullException>(() => ReasonletTranscript.ToCapture(null!, Details));
    }

    private static ReasonletCapture Capture(params ChatMessage[] messages)
    {
        var result = ReasonletTranscript.ToCapture(messages, Details);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }
}
