using System.Globalization;
using System.Text;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Abstractions;

public class GraphHashTests
{
    /// <summary>The canonical form of <see cref="GoldenGraph"/>. Part of the permanent hash contract.</summary>
    private const string GoldenCanonical =
        """{"edges":[{"from":"a","to":"b"},{"condition":"found","from":"a","properties":{"edge.weight":{"a":[3,1,2],"z":1.0}},"to":"b"},{"from":"b","to":"c"}],"nodes":[{"id":"a","name":"Search <é>","properties":{"tool.args":{"limit":10,"q":"x \"y\""}},"tool":"search","type":"tool"},{"id":"b","type":"decision"},{"id":"c","name":"Answer","type":"answer"}],"properties":{"graph.owner":"team","graph.tags":["b","a"]},"schemaVersion":1}""";

    /// <summary>The SHA-256 of <see cref="GoldenCanonical"/>. Never change it: stored hashes depend on it.</summary>
    private const string GoldenHash = "c805a1b9dd7493407f8b1263ed5b76a752787078f58410625201cf92886d157b";

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Props(params (string Key, string Json)[] entries)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, json) in entries)
        {
            props[key] = Json(json);
        }

        return props;
    }

    private static string Hash(ReasonletGraph graph)
    {
        var result = graph.ComputeContentHash();
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    /// <summary>A graph with nodes, edges and property keys deliberately out of canonical order.</summary>
    private static ReasonletGraph GoldenGraph() => new()
    {
        Nodes =
        [
            new ReasonletNode { Id = "c", Type = "answer", Name = "Answer" },
            new ReasonletNode
            {
                Id = "a",
                Type = "tool",
                Name = "Search <é>",
                Tool = "search",
                Properties = Props(("tool.args", """{"q":"x \"y\"","limit":10}""")),
            },
            new ReasonletNode { Id = "b", Type = "decision" },
        ],
        Edges =
        [
            new ReasonletEdge { From = "b", To = "c" },
            new ReasonletEdge
            {
                From = "a",
                To = "b",
                Condition = "found",
                Properties = Props(("edge.weight", """{"z":1.0,"a":[3,1,2]}""")),
            },
            new ReasonletEdge { From = "a", To = "b" },
        ],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
        Properties = Props(("graph.tags", """["b","a"]"""), ("graph.owner", "\"team\"")),
    };

    [Fact]
    public void Hash_is_64_lowercase_hex_characters()
    {
        var hash = Hash(TestData.Graph());

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f', $"unexpected '{c}'"));
    }

    [Fact]
    public void Reordering_nodes_gives_the_same_hash()
    {
        var graph = GoldenGraph();
        var reordered = graph with { Nodes = graph.Nodes.Reverse().ToList() };

        Assert.Equal(Hash(graph), Hash(reordered));
    }

    [Fact]
    public void Reordering_edges_gives_the_same_hash()
    {
        var graph = GoldenGraph();
        var reordered = graph with { Edges = graph.Edges.Reverse().ToList() };

        Assert.Equal(Hash(graph), Hash(reordered));
    }

    [Fact]
    public void Reordering_property_keys_gives_the_same_hash()
    {
        var first = TestData.Graph() with { Properties = Props(("a.x", "1"), ("a.y", "2")) };
        var second = TestData.Graph() with { Properties = Props(("a.y", "2"), ("a.x", "1")) };

        Assert.Equal(Hash(first), Hash(second));
    }

    [Fact]
    public void Reordering_nested_object_members_gives_the_same_hash()
    {
        var first = TestData.Graph() with { Properties = Props(("a.x", """{"p":{"m":1,"n":[{"b":1,"a":2}]},"o":2}""")) };
        var second = TestData.Graph() with { Properties = Props(("a.x", """{"o":2,"p":{"n":[{"a":2,"b":1}],"m":1}}""")) };

        Assert.Equal(Hash(first), Hash(second));
    }

    [Fact]
    public void Reordering_an_array_in_a_property_value_changes_the_hash()
    {
        var first = TestData.Graph() with { Properties = Props(("a.x", "[1,2]")) };
        var second = TestData.Graph() with { Properties = Props(("a.x", "[2,1]")) };

        Assert.NotEqual(Hash(first), Hash(second));
    }

    public static TheoryData<string> Changes =>
        ["node id", "node name", "node tool", "node type", "edge condition", "graph property", "node property", "edge property"];

    [Theory]
    [MemberData(nameof(Changes))]
    public void Changing_a_field_changes_the_hash(string change)
    {
        var graph = GoldenGraph();
        var changed = change switch
        {
            "node id" => graph with
            {
                Nodes = [graph.Nodes[0] with { Id = "d" }, graph.Nodes[1], graph.Nodes[2]],
                Edges = [new ReasonletEdge { From = "b", To = "d" }, graph.Edges[1], graph.Edges[2]],
            },
            "node name" => graph with { Nodes = [graph.Nodes[0] with { Name = "Answer!" }, graph.Nodes[1], graph.Nodes[2]] },
            "node tool" => graph with { Nodes = [graph.Nodes[0], graph.Nodes[1] with { Tool = "find" }, graph.Nodes[2]] },
            "node type" => graph with { Nodes = [graph.Nodes[0], graph.Nodes[1], graph.Nodes[2] with { Type = "tool" }] },
            "edge condition" => graph with { Edges = [graph.Edges[0], graph.Edges[1] with { Condition = "missing" }, graph.Edges[2]] },
            "graph property" => graph with { Properties = Props(("graph.tags", """["b","a"]"""), ("graph.owner", "\"other\"")) },
            "node property" => graph with
            {
                Nodes = [graph.Nodes[0], graph.Nodes[1] with { Properties = Props(("tool.args", """{"q":"x \"y\"","limit":11}""")) }, graph.Nodes[2]],
            },
            "edge property" => graph with
            {
                Edges = [graph.Edges[0], graph.Edges[1] with { Properties = Props(("edge.weight", """{"z":1.0,"a":[3,1]}""")) }, graph.Edges[2]],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };

        Assert.NotEqual(Hash(graph), Hash(changed));
    }

    /// <summary>
    /// A version-2 graph fails <see cref="ReasonletGraph.Validate"/>, so the public API cannot hash one. The internal
    /// writer does not validate, so this proves <c>schemaVersion</c> is part of the hashed form.
    /// </summary>
    [Fact]
    public void Changing_the_schema_version_changes_the_hash()
    {
        var graph = GoldenGraph();

        Assert.NotEqual(CanonicalGraphWriter.Hash(graph), CanonicalGraphWriter.Hash(graph with { SchemaVersion = 2 }));
    }

    [Fact]
    public void Escaped_and_unescaped_strings_hash_the_same()
    {
        var plain = TestData.Graph() with { Properties = Props(("a.x", "\"A\"")) };
        var escaped = TestData.Graph() with { Properties = Props(("a.x", "\"\\u0041\"")) };

        Assert.Equal(Hash(plain), Hash(escaped));
    }

    [Fact]
    public void Escaped_and_unescaped_member_names_hash_the_same()
    {
        var plain = TestData.Graph() with { Properties = Props(("a.x", """{"A":1}""")) };
        var escaped = TestData.Graph() with { Properties = Props(("a.x", """{"\u0041":1}""")) };

        Assert.Equal(Hash(plain), Hash(escaped));
    }

    [Fact]
    public void One_and_one_point_zero_hash_differently()
    {
        var integer = TestData.Graph() with { Properties = Props(("a.x", "1")) };
        var real = TestData.Graph() with { Properties = Props(("a.x", "1.0")) };

        Assert.NotEqual(Hash(integer), Hash(real));
    }

    [Fact]
    public void Null_and_empty_properties_hash_the_same()
    {
        var graph = TestData.Graph();
        var nulls = graph with
        {
            Properties = null,
            Nodes = [graph.Nodes[0] with { Properties = null }, graph.Nodes[1]],
            Edges = [graph.Edges[0] with { Properties = null }],
        };
        var empties = graph with
        {
            Properties = Props(),
            Nodes = [graph.Nodes[0] with { Properties = Props() }, graph.Nodes[1]],
            Edges = [graph.Edges[0] with { Properties = Props() }],
        };

        Assert.Equal(Hash(nulls), Hash(empties));
    }

    [Fact]
    public void Invalid_graph_returns_a_validation_failure()
    {
        var result = (TestData.Graph() with { Nodes = [] }).ComputeContentHash();

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
    }

    [Fact]
    public void Undefined_property_value_returns_a_validation_failure()
    {
        var graph = TestData.Graph() with
        {
            Properties = new Dictionary<string, JsonElement> { ["a.x"] = default },
        };

        var result = graph.ComputeContentHash();

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
    }

    [Fact]
    public void Golden_graph_has_the_pinned_canonical_form()
    {
        Assert.Equal(GoldenCanonical, Encoding.UTF8.GetString(CanonicalGraphWriter.Write(GoldenGraph())));
    }

    [Fact]
    public void Golden_graph_has_the_pinned_hash()
    {
        Assert.Equal(GoldenHash, Hash(GoldenGraph()));
    }

    [Fact]
    public void Golden_graph_has_the_pinned_hash_under_a_turkish_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

            Assert.Equal(GoldenHash, Hash(GoldenGraph()));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
