using System.Text.Json;
using Json.Schema;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Abstractions;

public class GraphSchemaTests
{
    private static readonly string SchemaText =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", "reasonlet-graph.v1.schema.json"));

    private static readonly JsonSchema Schema = JsonSchema.FromText(SchemaText);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static bool IsValid(string json) =>
        Schema.Evaluate(Json(json), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    private static ReasonletGraph Full() => new()
    {
        Nodes =
        [
            new ReasonletNode
            {
                Id = "n1",
                Type = "tool",
                Name = "Search",
                Tool = "search",
                Properties = new Dictionary<string, JsonElement> { ["node.retries"] = Json("3") },
            },
            new ReasonletNode { Id = "n2", Type = "answer" },
        ],
        Edges =
        [
            new ReasonletEdge
            {
                From = "n1",
                To = "n2",
                Condition = "found",
                Properties = new Dictionary<string, JsonElement> { ["edge.weight"] = Json("""{"x":[1,2.5,null,true]}""") },
            },
        ],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
        Properties = new Dictionary<string, JsonElement> { ["graph.owner"] = Json("\"team\"") },
    };

    private static ReasonletGraph Minimal() => new()
    {
        Nodes = [new ReasonletNode { Id = "only", Type = "step" }],
        Edges = [],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
    };

    public static TheoryData<string> Samples => ["plain", "minimal", "full"];

    [Theory]
    [MemberData(nameof(Samples))]
    public void SerializerOutput_ValidatesAgainstSchema(string which)
    {
        var graph = which switch { "plain" => TestData.Graph(), "minimal" => Minimal(), _ => Full() };

        Assert.True(IsValid(ReasonletGraphJson.Serialize(graph)));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t","colour":"red"}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{"from":"a","to":"a","weight":1}]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[],"extra":1}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t","properties":{"nodot":1}}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[],"properties":{"a..b":1}}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[],"properties":{".a.b":1}}""")]
    [InlineData("""{"schemaVersion":2,"nodes":[{"id":"a","type":"t"}],"edges":[]}""")]
    [InlineData("""{"nodes":[{"id":"a","type":"t"}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"  ","type":"t"}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a"}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{"from":"a"}]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{"from":"a","to":"a","condition":""}]}""")]
    public void InvalidDocument_FailsSchema(string json)
    {
        Assert.False(IsValid(json));
    }

    [Fact]
    public void TooManyNodesOrEdges_FailsSchema()
    {
        var nodes = string.Join(",", Enumerable.Repeat("""{"id":"a","type":"t"}""", ReasonletGraphLimits.MaxNodes + 1));
        var edges = string.Join(",", Enumerable.Repeat("""{"from":"a","to":"a"}""", ReasonletGraphLimits.MaxEdges + 1));

        Assert.False(IsValid($$"""{"schemaVersion":1,"nodes":[{{nodes}}],"edges":[]}"""));
        Assert.False(IsValid($$"""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{{edges}}]}"""));
    }

    [Fact]
    public void Schema_DeclaresPublishedId()
    {
        Assert.Equal(
            "https://github.com/MarcelRoozekrans/Mnemosyne.NET/schemas/reasonlet-graph.v1.schema.json",
            Json(SchemaText).GetProperty("$id").GetString());
    }
}
