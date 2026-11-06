using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

public sealed class ReasonletPromptFormatterTests
{
    private const string Header =
        "A procedure that worked for a similar question before. It is a suggestion: follow it only where it fits.";

    [Fact]
    public void ChainRendersTheSpecExample()
    {
        var text = ReasonletPromptFormatter.Format(Match(ChainGraph()));

        Assert.Equal(
            string.Join(
                "\n",
                Header,
                "Name: refund-order · match 0.82 · success 82% over 11 runs",
                "1. find_order (customerId, status)",
                "2. issue_refund (amount, orderId)"),
            text);
    }

    [Fact]
    public void NoArgumentsRendersEmptyParentheses()
    {
        var graph = Graph([Node("a", "tool", tool: "ping", arguments: "[]")], []);

        Assert.EndsWith("\n1. ping ()", ReasonletPromptFormatter.Format(Match(graph)), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingArgumentsPropertyOmitsTheList()
    {
        var graph = Graph([Node("a", "tool", tool: "ping")], []);

        Assert.EndsWith("\n1. ping", ReasonletPromptFormatter.Format(Match(graph)), StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsOfAnotherKindOmitTheList()
    {
        var graph = Graph(
            [
                Node("a", "tool", tool: "one", arguments: "\"x\""),
                Node("b", "tool", tool: "two", arguments: "[1, \"y\"]"),
            ],
            [new ReasonletEdge { From = "a", To = "b" }]);

        var steps = ReasonletPromptFormatter.Steps(graph);

        Assert.Equal(["one", "two"], steps.Select(s => s.Label));
        Assert.All(steps, s => Assert.Null(s.Arguments));
    }

    [Fact]
    public void BranchingGraphListsEveryNodeOnceInTopologicalOrder()
    {
        var graph = Graph(
            [
                Node("d", "answer"),
                Node("c", "tool", tool: "load"),
                Node("b", "decision", name: "choose"),
                Node("a", "retrieve", tool: "search", arguments: "[\"q\"]"),
            ],
            [Edge("a", "c"), Edge("a", "b"), Edge("b", "d"), Edge("c", "d")]);

        var steps = ReasonletPromptFormatter.Steps(graph);

        Assert.Equal(["search", "choose", "load", "answer"], steps.Select(s => s.Label));
        Assert.Equal(["q"], steps[0].Arguments);
    }

    [Fact]
    public void CyclicGraphThrows()
    {
        var graph = Graph([Node("a", "tool", tool: "x"), Node("b", "tool", tool: "y")], [Edge("a", "b"), Edge("b", "a")]);

        Assert.Throws<InvalidOperationException>(() => ReasonletPromptFormatter.Steps(graph));
    }

    [Fact]
    public void InvariantCultureIsUsed()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            var text = ReasonletPromptFormatter.Format(Match(ChainGraph()));

            Assert.Contains("match 0.82 · success 82% over", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ReasonletGraph ChainGraph()
    {
        var call1 = new FunctionCallContent("c1", "find_order", new Dictionary<string, object?> { ["status"] = 1, ["customerId"] = 2 });
        var call2 = new FunctionCallContent("c2", "issue_refund", new Dictionary<string, object?> { ["orderId"] = 1, ["amount"] = 2 });
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "refund my order"),
            new(ChatRole.Assistant, [call1]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant, [call2]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", "ok")]),
        };
        return ReasonletTranscript.ToGraph(messages).Value;
    }

    private static ReasonletMatch Match(ReasonletGraph graph) => new()
    {
        Reasonlet = new ReasonletDefinition
        {
            Id = ReasonletId.New(),
            Scope = new ReasonletScope("support"),
            Name = "refund-order",
            Intent = "refund an order",
            Version = "1",
            Graph = graph,
            Provenance = new ReasonletProvenance { Source = "test" },
            Status = ReasonletStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
        },
        Score = new ReasonletScore(Similarity: 0.82, SuccessRate: 0.82, Freshness: 1, Total: 0.8),
        Evidence = new ReasonletEvidence { Runs = 11, JudgedRuns = 11, UsesParentPrior = false },
        LineageId = ReasonletId.New(),
    };

    private static ReasonletGraph Graph(ReasonletNode[] nodes, ReasonletEdge[] edges) =>
        new() { Nodes = nodes, Edges = edges, SchemaVersion = ReasonletGraph.CurrentSchemaVersion };

    private static ReasonletEdge Edge(string from, string to) => new() { From = from, To = to };

    private static ReasonletNode Node(string id, string type, string? tool = null, string? name = null, string? arguments = null) =>
        new()
        {
            Id = id,
            Type = type,
            Tool = tool,
            Name = name,
            Properties = arguments is null
                ? null
                : new Dictionary<string, JsonElement> { ["mnemosyne.arguments"] = JsonDocument.Parse(arguments).RootElement.Clone() },
        };
}
