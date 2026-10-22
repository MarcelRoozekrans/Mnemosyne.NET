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
    public void TooManyNodes_FailsSchema()
    {
        var nodes = string.Join(",", Enumerable.Repeat("""{"id":"a","type":"t"}""", ReasonletGraphLimits.MaxNodes + 1));

        Assert.False(IsValid($$"""{"schemaVersion":1,"nodes":[{{nodes}}],"edges":[]}"""));
    }

    [Fact]
    public void TooManyEdges_FailsSchema()
    {
        var edges = string.Join(",", Enumerable.Repeat("""{"from":"a","to":"a"}""", ReasonletGraphLimits.MaxEdges + 1));

        Assert.False(IsValid($$"""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{{edges}}]}"""));
    }

    private const string WithNulls =
        """{"schemaVersion":1,"nodes":[{"id":"a","type":"t","name":null,"tool":null,"properties":null},{"id":"b","type":"t"}],"edges":[{"from":"a","to":"b","condition":null,"properties":null}],"properties":null}""";

    private const string WithoutNulls =
        """{"schemaVersion":1,"nodes":[{"id":"a","type":"t"},{"id":"b","type":"t"}],"edges":[{"from":"a","to":"b"}]}""";

    [Fact]
    public void ExplicitNullOptionals_PassSchema()
    {
        Assert.True(IsValid(WithNulls));
    }

    [Fact]
    public void ExplicitNullOptionals_DeserializeAndHashLikeAbsent()
    {
        var withNulls = ReasonletGraphJson.Deserialize(WithNulls);
        var without = ReasonletGraphJson.Deserialize(WithoutNulls);

        Assert.True(withNulls.IsSuccess, withNulls.IsFailure ? withNulls.Error.Message : null);
        Assert.True(without.IsSuccess);
        Assert.Equal(without.Value.ComputeContentHash(), withNulls.Value.ComputeContentHash());
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":null}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{"from":null,"to":"a"}]}""")]
    public void ExplicitNullRequired_FailsSchema(string json)
    {
        Assert.False(IsValid(json));
    }

    [Fact]
    public void NonBlankClass_MatchesCharIsWhiteSpaceForEveryChar()
    {
        var pattern = Json(SchemaText).GetProperty("$defs").GetProperty("nonBlankString").GetProperty("pattern").GetString()!;
        var regex = new System.Text.RegularExpressions.Regex(pattern);

        for (var c = 0; c <= 0xFFFF; c++)
        {
            Assert.Equal(!char.IsWhiteSpace((char)c), regex.IsMatch(((char)c).ToString()));
        }
    }

    [Theory]
    [InlineData("\\u0085", false)]
    [InlineData("\\uFEFF", true)]
    public void Id_OfOnlyOneChar_AgreesWithValidate(string escaped, bool valid)
    {
        var json = $$"""{"schemaVersion":1,"nodes":[{"id":"{{escaped}}","type":"t"}],"edges":[]}""";

        Assert.Equal(valid, IsValid(json));
        Assert.Equal(valid, ReasonletGraphJson.Deserialize(json).IsSuccess);
    }

    [Fact]
    public void Schema_DeclaresPublishedId()
    {
        Assert.Equal(
            "https://github.com/MarcelRoozekrans/Mnemosyne.NET/schemas/reasonlet-graph.v1.schema.json",
            Json(SchemaText).GetProperty("$id").GetString());
    }

    /// <summary>A document whose <paramref name="member"/> holds <paramref name="value"/>, which needs no escaping.</summary>
    private static string WithMember(string member, string value)
    {
        var quoted = "\"" + value + "\"";
        return member switch
        {
            "id" => $$"""{"schemaVersion":1,"nodes":[{"id":{{quoted}},"type":"t"}],"edges":[]}""",
            "type" => $$"""{"schemaVersion":1,"nodes":[{"id":"a","type":{{quoted}}}],"edges":[]}""",
            "name" => $$"""{"schemaVersion":1,"nodes":[{"id":"a","type":"t","name":{{quoted}}}],"edges":[]}""",
            "tool" => $$"""{"schemaVersion":1,"nodes":[{"id":"a","type":"t","tool":{{quoted}}}],"edges":[]}""",
            "from" => $$"""{"schemaVersion":1,"nodes":[{"id":{{quoted}},"type":"t"}],"edges":[{"from":{{quoted}},"to":{{quoted}}}]}""",
            "to" => $$"""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"},{"id":{{quoted}},"type":"t"}],"edges":[{"from":"a","to":{{quoted}}}]}""",
            _ => $$"""{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[{"from":"a","to":"a","condition":{{quoted}}}]}""",
        };
    }

    /// <remarks>
    /// JSON Schema counts <c>maxLength</c> in code points, as <see cref="ReasonletGraph.Validate"/> does. JsonSchema.Net
    /// 9.4.1 counts text elements instead (<c>StringInfo.LengthInTextElements</c>), so this test does not probe a
    /// combining sequence over the limit: that library would accept it. A supplementary character is one code point
    /// and one text element, so the astral cases do prove that UTF-16 units are not counted.
    /// <see cref="GraphValidationTests"/> covers the combining case for Validate.
    /// </remarks>
    [Theory]
    [InlineData("id", ReasonletGraphLimits.MaxIdentifierLength)]
    [InlineData("type", ReasonletGraphLimits.MaxIdentifierLength)]
    [InlineData("tool", ReasonletGraphLimits.MaxIdentifierLength)]
    [InlineData("from", ReasonletGraphLimits.MaxIdentifierLength)]
    [InlineData("to", ReasonletGraphLimits.MaxIdentifierLength)]
    [InlineData("name", ReasonletGraphLimits.MaxTextLength)]
    [InlineData("condition", ReasonletGraphLimits.MaxTextLength)]
    public void LengthLimits_MatchValidate(string member, int limit)
    {
        var astral = string.Concat(Enumerable.Repeat("\U0001F600", limit));
        var atLimit = new[] { new string('x', limit), astral };
        var overLimit = new[] { new string('x', limit + 1), astral + "x" };

        foreach (var value in atLimit)
        {
            Assert.True(IsValid(WithMember(member, value)));
            Assert.True(ReasonletGraphJson.Deserialize(WithMember(member, value)).IsSuccess);
        }

        foreach (var value in overLimit)
        {
            Assert.False(IsValid(WithMember(member, value)));
            Assert.True(ReasonletGraphJson.Deserialize(WithMember(member, value)).IsFailure);
        }
    }
}
