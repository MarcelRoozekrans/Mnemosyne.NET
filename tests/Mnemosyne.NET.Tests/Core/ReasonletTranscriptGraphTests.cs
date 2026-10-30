using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The tool-shape graph <see cref="ReasonletTranscript.ToGraph"/> builds from a chat transcript.</summary>
public sealed class ReasonletTranscriptGraphTests
{
    [Fact]
    public void ASingleCallBecomesOneToolStep()
    {
        var graph = Graph(User("Find order 7"), Calls(("c1", "orders.get", ["id"])), Results("c1"));

        var node = Assert.Single(graph.Nodes);
        Assert.Equal("step-1", node.Id);
        Assert.Equal("tool", node.Type);
        Assert.Equal("orders.get", node.Tool);
        Assert.Null(node.Name);
        Assert.Equal("""["id"]""", Arguments(node));
        Assert.Empty(graph.Edges);
        Assert.Equal(ReasonletGraph.CurrentSchemaVersion, graph.SchemaVersion);
        Assert.Null(graph.Properties);
        Assert.True(graph.Validate().IsSuccess);
    }

    [Fact]
    public void StepsChainInOrder()
    {
        var graph = Graph(
            User("Q"),
            Calls(("c1", "A", [])), Results("c1"),
            Calls(("c2", "B", [])), Results("c2"),
            Calls(("c3", "C", [])), Results("c3"));

        Assert.Equal(["step-1", "step-2", "step-3"], graph.Nodes.Select(n => n.Id));
        Assert.Equal(["A", "B", "C"], graph.Nodes.Select(n => n.Tool));
        Assert.Equal([("step-1", "step-2"), ("step-2", "step-3")], graph.Edges.Select(e => (e.From, e.To)));
        Assert.All(graph.Edges, e => Assert.Null(e.Condition));
        Assert.All(graph.Edges, e => Assert.Null(e.Properties));
    }

    [Fact]
    public void ArgumentNamesAreSortedAndValuesIgnored()
    {
        ChatMessage[] Run() => [User("Q"), Calls(("c1", "search", ["b", "a", "é"])), Results("c1")];

        var first = Graph(Run());

        Assert.Equal("""["a","b","é"]""", Arguments(Assert.Single(first.Nodes)));
        Assert.Equal(Hash(first), Hash(Graph(Run())));
    }

    [Fact]
    public void NullArgumentsGiveAnEmptyArray()
    {
        var graph = Graph(
            User("Q"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "ping", arguments: null)]),
            Results("c1"));

        Assert.Equal("[]", Arguments(Assert.Single(graph.Nodes)));
    }

    [Fact]
    public void ParallelCallsAreSortedByToolThenArguments()
    {
        var graph = Graph(User("Q"), Calls(("c1", "z", ["x"]), ("c2", "a", ["y"]), ("c3", "a", ["x"])), Results("c1", "c2", "c3"));
        var reordered = Graph(User("Q"), Calls(("c3", "a", ["x"]), ("c1", "z", ["x"]), ("c2", "a", ["y"])), Results("c2", "c3", "c1"));

        Assert.Equal(["a", "a", "z"], graph.Nodes.Select(n => n.Tool));
        Assert.Equal(["""["x"]""", """["y"]""", """["x"]"""], graph.Nodes.Select(Arguments));
        Assert.Equal(Hash(graph), Hash(reordered));
    }

    [Fact]
    public void AShorterArgumentListSortsFirst()
    {
        var graph = Graph(User("Q"), Calls(("c1", "a", ["x", "y"]), ("c2", "a", ["x"])), Results("c1", "c2"));

        Assert.Equal(["""["x"]""", """["x","y"]"""], graph.Nodes.Select(Arguments));
    }

    [Fact]
    public void ConsecutiveEqualStepsCollapse()
    {
        var graph = Graph(
            User("Q"),
            Calls(("c1", "search", ["q"])), Results("c1"),
            Calls(("c2", "search", ["q"])), Results("c2"),
            Calls(("c3", "search", ["q"])), Results("c3"));

        Assert.Equal("search", Assert.Single(graph.Nodes).Tool);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void CollapseHappensAfterTheParallelSort()
    {
        var graph = Graph(User("Q"), Calls(("c1", "b", []), ("c2", "a", []), ("c3", "a", [])), Results("c1", "c2", "c3"));

        Assert.Equal(["a", "b"], graph.Nodes.Select(n => n.Tool));
    }

    [Fact]
    public void NonConsecutiveRepeatsStaySeparate()
    {
        var graph = Graph(
            User("Q"),
            Calls(("c1", "A", [])), Results("c1"),
            Calls(("c2", "B", [])), Results("c2"),
            Calls(("c3", "A", [])), Results("c3"));

        Assert.Equal(["A", "B", "A"], graph.Nodes.Select(n => n.Tool));
    }

    [Fact]
    public void DifferentArgumentNamesStaySeparate()
    {
        var graph = Graph(
            User("Q"),
            Calls(("c1", "search", ["q"])), Results("c1"),
            Calls(("c2", "search", ["q", "page"])), Results("c2"));

        Assert.Equal(["""["q"]""", """["page","q"]"""], graph.Nodes.Select(Arguments));
    }

    [Fact]
    public void ToolNamesDifferingInCaseStaySeparate()
    {
        var graph = Graph(
            User("Q"),
            Calls(("c1", "Search", ["q"])), Results("c1"),
            Calls(("c2", "search", ["q"])), Results("c2"));

        Assert.Equal(["Search", "search"], graph.Nodes.Select(n => n.Tool));
    }

    [Fact]
    public void FailedAndUnansweredCallsAreLeftOut()
    {
        var graph = Graph(
            User("Q"),
            Calls(("c1", "broken", []), ("c2", "silent", []), ("c3", "ok", [])),
            Failed("c1"),
            Results("c3"));

        Assert.Equal("ok", Assert.Single(graph.Nodes).Tool);
    }

    [Fact]
    public void ARetryAfterAFailureHashesLikeACleanRun()
    {
        var retried = Graph(
            User("Q"),
            Calls(("c1", "orders.get", ["id"])), Failed("c1"),
            Calls(("c2", "orders.get", ["id"])), Results("c2"),
            Calls(("c3", "payments.refund", ["orderId"])), Results("c3"));
        var clean = Graph(
            User("Q"),
            Calls(("d1", "orders.get", ["id"])), Results("d1"),
            Calls(("d2", "payments.refund", ["orderId"])), Results("d2"));

        Assert.Equal(Hash(clean), Hash(retried));
    }

    [Fact]
    public void TheFirstResultForACallIdCounts()
    {
        var okFirst = Graph(User("Q"), Calls(("c1", "A", []), ("c2", "B", [])), Results("c1", "c2"), Failed("c1"));
        var failedFirst = Graph(User("Q"), Calls(("c1", "A", []), ("c2", "B", [])), Failed("c1"), Results("c1", "c2"));

        Assert.Equal(["A", "B"], okFirst.Nodes.Select(n => n.Tool));
        Assert.Equal(["B"], failedFirst.Nodes.Select(n => n.Tool));
    }

    [Fact]
    public void CallsBeforeTheQuestionAreIgnored()
    {
        var graph = Graph(
            User("Q1"),
            Calls(("c1", "A", [])), Results("c1"),
            User("Q2"),
            Calls(("c2", "B", [])), Results("c2"));

        Assert.Equal("B", Assert.Single(graph.Nodes).Tool);
    }

    [Fact]
    public void AResultInTheRunForACallBeforeTheQuestionDoesNotCount()
    {
        var result = ReasonletTranscript.ToGraph([User("Q1"), Calls(("c1", "A", [])), User("Q2"), Results("c1")]);

        Assert.True(result.IsFailure);
        Assert.Equal(TranscriptSkipReason.NoToolCalls, result.Error);
    }

    [Fact]
    public void ACallInANonAssistantMessageDoesNotCount()
    {
        var result = ReasonletTranscript.ToGraph(
            [User("Q"), new ChatMessage(ChatRole.Tool, [new FunctionCallContent("c1", "A")]), Results("c1")]);

        Assert.Equal(TranscriptSkipReason.NoToolCalls, result.Error);
    }

    public static TheoryData<ChatMessage[]> NoQuestionTranscripts() => new()
    {
        Array.Empty<ChatMessage>(),
        new[] { new ChatMessage(ChatRole.Assistant, "hi"), Calls(("c1", "A", [])), Results("c1") },
        new[] { User("  "), Calls(("c1", "A", [])), Results("c1") },
    };

    [Theory]
    [MemberData(nameof(NoQuestionTranscripts))]
    public void NoUserTextGivesNoQuestion(ChatMessage[] messages)
    {
        var result = ReasonletTranscript.ToGraph(messages);

        Assert.True(result.IsFailure);
        Assert.Equal(TranscriptSkipReason.NoQuestion, result.Error);
    }

    public static TheoryData<ChatMessage[]> NoToolCallTranscripts() => new()
    {
        new[] { User("Q") },
        new[] { User("Q"), new ChatMessage(ChatRole.Assistant, "Just an answer.") },
        new[] { User("Q"), Calls(("c1", "A", [])), Failed("c1") },
    };

    [Theory]
    [MemberData(nameof(NoToolCallTranscripts))]
    public void NoSuccessfulCallGivesNoToolCalls(ChatMessage[] messages)
    {
        var result = ReasonletTranscript.ToGraph(messages);

        Assert.True(result.IsFailure);
        Assert.Equal(TranscriptSkipReason.NoToolCalls, result.Error);
    }

    [Fact]
    public void CallIdsAndMessageIdsDoNotReachTheHash()
    {
        ChatMessage[] Run(string prefix, string secondTool) =>
        [
            Stamp(User("Q"), prefix),
            Stamp(Calls(($"{prefix}-1", "orders.get", ["id"])), prefix), Stamp(Results($"{prefix}-1"), prefix),
            Stamp(Calls(($"{prefix}-2", secondTool, ["orderId"])), prefix), Stamp(Results($"{prefix}-2"), prefix),
        ];

        var first = Hash(Graph(Run("x", "payments.refund")));

        Assert.Equal(first, Hash(Graph(Run("y", "payments.refund"))));
        Assert.NotEqual(first, Hash(Graph(Run("x", "payments.void"))));
    }

    [Fact]
    public void AnOversizedRunStillBuildsAndFailsValidation()
    {
        var count = ReasonletGraphLimits.MaxNodes + 1;
        var messages = new List<ChatMessage> { User("Q") };
        for (var i = 0; i < count; i++)
        {
            messages.Add(Calls(($"c{i}", $"t{i}", [])));
            messages.Add(Results($"c{i}"));
        }

        var result = ReasonletTranscript.ToGraph(messages);

        Assert.True(result.IsSuccess);
        Assert.Equal(count, result.Value.Nodes.Count);
        Assert.True(result.Value.Validate().IsFailure);
    }

    [Fact]
    public void NullMessagesThrow()
    {
        Assert.Throws<ArgumentNullException>(() => ReasonletTranscript.ToGraph(null!));
        Assert.Throws<ArgumentNullException>(() => ReasonletTranscript.ToGraph([User("Q"), null!]));
    }

    internal static ChatMessage User(string text) => new(ChatRole.User, text);

    internal static ChatMessage Calls(params (string Id, string Name, string[] Args)[] calls) => new(
        ChatRole.Assistant,
        [.. calls.Select(c => (AIContent)new FunctionCallContent(
            c.Id,
            c.Name,
            c.Args.ToDictionary(a => a, _ => (object?)Guid.NewGuid().ToString())))]);

    internal static ChatMessage Results(params string[] ids) =>
        new(ChatRole.Tool, [.. ids.Select(id => (AIContent)new FunctionResultContent(id, "ok"))]);

    internal static ChatMessage Failed(string id) =>
        new(ChatRole.Tool, [new FunctionResultContent(id, null) { Exception = new InvalidOperationException("boom") }]);

    internal static string Hash(ReasonletGraph graph)
    {
        var hash = graph.ComputeContentHash();
        Assert.True(hash.IsSuccess, hash.IsFailure ? hash.Error.Message : null);
        return hash.Value;
    }

    private static ReasonletGraph Graph(params IEnumerable<ChatMessage> messages)
    {
        var result = ReasonletTranscript.ToGraph(messages);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    /// <summary>The node's argument names as compact JSON with unescaped text, so a test reads them as written.</summary>
    private static string Arguments(ReasonletNode node) =>
        "[" + string.Join(",", node.Properties!["mnemosyne.arguments"].EnumerateArray().Select(e => $"\"{e.GetString()}\"")) + "]";

    private static ChatMessage Stamp(ChatMessage message, string prefix)
    {
        message.MessageId = $"{prefix}-{Guid.NewGuid():N}";
        message.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(prefix.Length);
        return message;
    }
}
