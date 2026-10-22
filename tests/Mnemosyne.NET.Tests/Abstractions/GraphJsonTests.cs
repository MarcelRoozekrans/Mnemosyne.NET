using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Abstractions;

public class GraphJsonTests
{
    private const string Valid = """{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1}""";

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static MnemosyneError AssertInvalid(string json)
    {
        var r = ReasonletGraphJson.Deserialize(json);
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        return r.Error;
    }

    /// <summary>Returns <see cref="Valid"/> with a graph property <c>a.b</c> set to <paramref name="valueJson"/>.</summary>
    private static string WithGraphProperty(string valueJson) =>
        """{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1,"properties":{"a.b":""" + valueJson + "}}";

    private static ReasonletGraph GraphWithPropertiesOnAllLevels() => new()
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

    public static TheoryData<string> RoundTripGraphs => ["plain", "properties"];

    [Theory]
    [MemberData(nameof(RoundTripGraphs))]
    public void RoundTrip_SucceedsAndValidates(string which)
    {
        var graph = which == "plain" ? TestData.Graph() : GraphWithPropertiesOnAllLevels();
        var json = ReasonletGraphJson.Serialize(graph);

        var r = ReasonletGraphJson.Deserialize(json);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Message : null);
        Assert.True(r.Value.Validate().IsSuccess);
        Assert.Equal(json, ReasonletGraphJson.Serialize(r.Value));
        Assert.Equal(graph.Nodes.Count, r.Value.Nodes.Count);
        Assert.Equal(graph.Edges.Count, r.Value.Edges.Count);
    }

    [Fact]
    public void RoundTrip_PreservesPropertyValues()
    {
        var r = ReasonletGraphJson.Deserialize(ReasonletGraphJson.Serialize(GraphWithPropertiesOnAllLevels()));

        Assert.True(r.IsSuccess);
        Assert.Equal("team", r.Value.Properties!["graph.owner"].GetString());
        Assert.Equal(3, r.Value.Nodes[0].Properties!["node.retries"].GetInt32());
        Assert.Equal(2.5, r.Value.Edges[0].Properties!["edge.weight"].GetProperty("x")[1].GetDouble());
        Assert.Equal("search", r.Value.Nodes[0].Tool);
        Assert.Equal("found", r.Value.Edges[0].Condition);
    }

    [Fact]
    public void Serialize_WritesExactJson()
    {
        var graph = new ReasonletGraph
        {
            Nodes =
            [
                new ReasonletNode
                {
                    Id = "a",
                    Type = "tool",
                    Name = "Zoek <é> & \"x\"",
                    Tool = "search",
                    Properties = new Dictionary<string, JsonElement>(),
                },
                new ReasonletNode { Id = "b", Type = "answer" },
            ],
            Edges = [new ReasonletEdge { From = "a", To = "b", Condition = "found" }],
            SchemaVersion = 1,
            Properties = new Dictionary<string, JsonElement> { ["meta.owner"] = Json("\"x\"") },
        };

        var json = ReasonletGraphJson.Serialize(graph);

        Assert.Equal(
            """{"nodes":[{"id":"a","type":"tool","name":"Zoek <é> & \"x\"","tool":"search"},{"id":"b","type":"answer"}],"edges":[{"from":"a","to":"b","condition":"found"}],"schemaVersion":1,"properties":{"meta.owner":"x"}}""",
            json);
    }

    [Fact]
    public void Serialize_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ReasonletGraphJson.Serialize(null!));

    [Fact]
    public void Deserialize_Valid_Succeeds() => Assert.True(ReasonletGraphJson.Deserialize(Valid).IsSuccess);

    [Fact]
    public void MalformedJson_Fails() => AssertInvalid("{");

    [Fact]
    public void NullJsonText_Fails() => AssertInvalid(null!);

    [Fact]
    public void JsonNullLiteral_Fails() => AssertInvalid("null");

    [Fact]
    public void LoneSurrogateInText_Fails() =>
        Assert.Equal("$: text is not valid UTF-16", AssertInvalid("{\"nodes\":[{\"id\":\"\uD800\",\"type\":\"step\"}],\"edges\":[],\"schemaVersion\":1}").Message);

    [Fact]
    public void MissingNodes_Fails() => AssertInvalid("""{"edges":[],"schemaVersion":1}""");

    [Fact]
    public void UnknownTopLevelMember_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1,"extra":1}""");
        Assert.Contains("extra", error.Message);
    }

    [Fact]
    public void UnknownNodeMember_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step","colour":"red"}],"edges":[],"schemaVersion":1}""");
        Assert.StartsWith("$.nodes[0]", error.Message);
    }

    [Fact]
    public void PascalCaseMember_Fails() =>
        AssertInvalid("""{"Nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1}""");

    [Fact]
    public void NullRequiredString_Fails() =>
        AssertInvalid("""{"nodes":[{"id":null,"type":"step"}],"edges":[],"schemaVersion":1}""");

    [Fact]
    public void UnsupportedSchemaVersion_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":2}""");
        Assert.StartsWith("SchemaVersion: ", error.Message);
    }

    [Fact]
    public void DuplicateTopLevelMember_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1,"schemaVersion":1}""");
        Assert.Contains("schemaVersion", error.Message);
    }

    [Fact]
    public void DuplicateNodeMember_Fails() =>
        AssertInvalid("""{"nodes":[{"id":"a","type":"step","id":"b"}],"edges":[],"schemaVersion":1}""");

    [Fact]
    public void DuplicateKeyInProperties_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1,"properties":{"a.b":1,"a.b":2}}""");
        Assert.Contains("a.b", error.Message);
    }

    [Fact]
    public void DuplicateKeyInNestedPropertyValue_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step","properties":{"a.b":{"x":1,"x":2}}}],"edges":[],"schemaVersion":1}""");
        Assert.Contains("x", error.Message);
    }

    [Fact]
    public void DuplicateKeyDeepInsideArrayInProperties_Fails() =>
        AssertInvalid("""{"nodes":[{"id":"a","type":"step"}],"edges":[{"from":"a","to":"a","properties":{"a.b":[{"y":{"x":1,"x":2}}]}}],"schemaVersion":1}""");

    [Fact]
    public void SameKeyInSiblingObjects_IsNotADuplicate() =>
        Assert.True(ReasonletGraphJson.Deserialize(
            """{"nodes":[{"id":"a","type":"step","properties":{"a.b":[{"x":1},{"x":2}],"a.c":{"x":{"x":1}}}}],"edges":[],"schemaVersion":1}""").IsSuccess);

    [Fact]
    public void ThousandLevelsDeep_FailsWithoutStackOverflow()
    {
        var deep = new string('[', 1000) + "1" + new string(']', 1000);
        AssertInvalid(WithGraphProperty(deep));
    }

    [Fact]
    public void ThousandLevelsDeepObjects_FailsWithoutStackOverflow()
    {
        var deep = string.Concat(Enumerable.Repeat("""{"x":""", 1000)) + "1" + new string('}', 1000);
        AssertInvalid(WithGraphProperty(deep));
    }

    [Fact]
    public void PropertyValueAtMaxPropertyDepth_RoundTripsOnEveryLevel()
    {
        var levels = ReasonletGraphLimits.MaxPropertyDepth;
        var value = Json(new string('[', levels - 1) + "1" + new string(']', levels - 1));
        var props = new Dictionary<string, JsonElement> { ["deep.value"] = value };
        var graph = new ReasonletGraph
        {
            Nodes = [new ReasonletNode { Id = "a", Type = "step", Properties = props }],
            Edges = [new ReasonletEdge { From = "a", To = "a", Properties = props }],
            SchemaVersion = 1,
            Properties = props,
        };
        Assert.True(graph.Validate().IsSuccess);

        var r = ReasonletGraphJson.Deserialize(ReasonletGraphJson.Serialize(graph));

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Message : null);
    }

    [Theory]
    [InlineData("graph", "Properties")]
    [InlineData("node", "Nodes[0].Properties")]
    [InlineData("edge", "Edges[0].Properties")]
    public void PropertyValueOneLevelTooDeep_FailsValidationWithPropertyPath(string level, string path)
    {
        var levels = ReasonletGraphLimits.MaxPropertyDepth + 1;
        var deep = """{"deep.value":""" + new string('[', levels - 1) + "1" + new string(']', levels - 1) + "}";
        var json = level switch
        {
            "graph" => """{"nodes":[{"id":"a","type":"step"}],"edges":[],"schemaVersion":1,"properties":""" + deep + "}",
            "node" => """{"nodes":[{"id":"a","type":"step","properties":""" + deep + """}],"edges":[],"schemaVersion":1}""",
            _ => """{"nodes":[{"id":"a","type":"step"}],"edges":[{"from":"a","to":"a","properties":""" + deep + """}],"schemaVersion":1}""",
        };

        var error = AssertInvalid(json);

        Assert.StartsWith(path + "[\"deep.value\"]: ", error.Message);
    }

    [Fact]
    public void EscapedLoneSurrogateInPropertyValue_Fails()
    {
        var error = AssertInvalid(WithGraphProperty("\"\\uD800\""));
        Assert.StartsWith("Properties[\"a.b\"]: ", error.Message);
    }

    /// <remarks>
    /// The reader rejects this before a graph exists, because duplicate detection unescapes member names.
    /// <see cref="GraphValidationTests"/> covers the same element reaching <see cref="ReasonletGraph.Validate"/> directly.
    /// </remarks>
    [Fact]
    public void EscapedLoneSurrogateInNestedMemberName_Fails()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step","properties":{"a.b":{"\uD800":1}}}],"edges":[],"schemaVersion":1}""");
        Assert.StartsWith("$.nodes[0].properties['a.b']: ", error.Message);
    }

    [Fact]
    public void EscapedSurrogatePairInPropertyValue_RoundTrips()
    {
        var r = ReasonletGraphJson.Deserialize(WithGraphProperty("\"\\uD83D\\uDE00\""));

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Message : null);
        Assert.Equal("\U0001F600", r.Value.Properties!["a.b"].GetString());
        var again = ReasonletGraphJson.Deserialize(ReasonletGraphJson.Serialize(r.Value));
        Assert.True(again.IsSuccess);
        Assert.Equal("\U0001F600", again.Value.Properties!["a.b"].GetString());
    }

    [Fact]
    public void JsonError_HasPathOnceAndNoNamespace()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step","colour":"red"}],"edges":[],"schemaVersion":1}""");

        Assert.StartsWith("$.nodes[0].colour: ", error.Message);
        Assert.DoesNotContain("Path:", error.Message);
        Assert.DoesNotContain("LineNumber", error.Message);
        Assert.DoesNotContain("Mnemosyne.NET.Abstractions.", error.Message);
    }

    [Fact]
    public void JsonErrorWithoutPath_UsesRoot()
    {
        var error = AssertInvalid("{");

        Assert.StartsWith("$: ", error.Message);
        Assert.DoesNotContain("LineNumber", error.Message);
    }

    [Fact]
    public void TenMegabyteString_FailsWithoutThrowing()
    {
        var huge = new string('x', 10 * 1024 * 1024);
        var error = AssertInvalid(WithGraphProperty("\"" + huge + "\""));
        Assert.StartsWith("Properties: ", error.Message);
    }

    [Fact]
    public void EdgeToMissingNode_FailsWithPath()
    {
        var error = AssertInvalid("""{"nodes":[{"id":"a","type":"step"}],"edges":[{"from":"a","to":"zz"}],"schemaVersion":1}""");
        Assert.StartsWith("Edges[0].To: ", error.Message);
    }

    [Fact]
    public void NullNodeElement_Fails()
    {
        var error = AssertInvalid("""{"nodes":[null],"edges":[],"schemaVersion":1}""");
        Assert.StartsWith("Nodes[0]: ", error.Message);
    }
}
