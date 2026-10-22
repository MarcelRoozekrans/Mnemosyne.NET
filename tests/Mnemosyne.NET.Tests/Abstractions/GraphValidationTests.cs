using System.Text;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class GraphValidationTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    private static ReasonletNode Node(string id, IReadOnlyDictionary<string, JsonElement>? props = null) =>
        new() { Id = id, Type = "step", Properties = props };

    private static ReasonletGraph Graph(IReadOnlyList<ReasonletNode> nodes, IReadOnlyList<ReasonletEdge>? edges = null) =>
        new() { Nodes = nodes, Edges = edges ?? [], SchemaVersion = 1 };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>A scalar is level 1, so a value of <paramref name="levels"/> levels is <paramref name="levels"/> - 1 arrays around a scalar.</summary>
    private static JsonElement Nested(int levels) =>
        Json(new string('[', levels - 1) + "1" + new string(']', levels - 1));

    [Fact]
    public void UnsupportedSchemaVersion_Fails()
    {
        var r = (Graph([Node("a")]) with { SchemaVersion = 2 }).Validate();
        AssertInvalid(r, "SchemaVersion");
        Assert.Contains("unsupported schema version", r.Error.Message);
    }

    [Fact]
    public void EmptyNodes_Fails() => AssertInvalid(Graph([]).Validate(), "Nodes");

    [Fact]
    public void DuplicateNodeId_Fails() =>
        AssertInvalid(Graph([Node("a"), Node("a")]).Validate(), "Nodes[1].Id");

    [Fact]
    public void EdgeToMissingNode_Fails() =>
        AssertInvalid(Graph([Node("a")], [new ReasonletEdge { From = "a", To = "zz" }]).Validate(), "Edges[0].To");

    [Fact]
    public void EdgeFromMissingNode_Fails() =>
        AssertInvalid(Graph([Node("a")], [new ReasonletEdge { From = "zz", To = "a" }]).Validate(), "Edges[0].From");

    [Fact]
    public void BlankCondition_Fails() =>
        AssertInvalid(Graph([Node("a")], [new ReasonletEdge { From = "a", To = "a", Condition = " " }]).Validate(), "Edges[0].Condition");

    [Fact]
    public void NullCondition_Passes() =>
        Assert.True(Graph([Node("a")], [new ReasonletEdge { From = "a", To = "a", Condition = null }]).Validate().IsSuccess);

    [Fact]
    public void DuplicateEdge_Fails() =>
        AssertInvalid(
            Graph([Node("a"), Node("b")], [new ReasonletEdge { From = "a", To = "b" }, new ReasonletEdge { From = "a", To = "b" }]).Validate(),
            "Edges[1]");

    [Fact]
    public void SameEndpointsDifferentConditions_Passes() =>
        Assert.True(Graph(
            [Node("a"), Node("b")],
            [new ReasonletEdge { From = "a", To = "b", Condition = "x" }, new ReasonletEdge { From = "a", To = "b", Condition = "y" }]).Validate().IsSuccess);

    [Fact]
    public void SelfLoopAndCycle_Pass()
    {
        var edges = new[]
        {
            new ReasonletEdge { From = "a", To = "a" },
            new ReasonletEdge { From = "a", To = "b" },
            new ReasonletEdge { From = "b", To = "a" },
        };
        Assert.True(Graph([Node("a"), Node("b")], edges).Validate().IsSuccess);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("k")]
    [InlineData(".")]
    [InlineData("a.")]
    [InlineData(".a")]
    [InlineData("a..b")]
    [InlineData("")]
    public void PropertyKeyGrammar_Enforced(string key)
    {
        var props = new Dictionary<string, JsonElement> { [key] = Json("1") };
        AssertInvalid(Graph([Node("a", props)]).Validate(), $"Nodes[0].Properties[\"{key}\"]");
    }

    [Theory]
    [InlineData("daedalus.process")]
    [InlineData("a.b.c")]
    public void ValidDottedKeys_Pass(string key) =>
        Assert.True(Graph([Node("a", new Dictionary<string, JsonElement> { [key] = Json("1") })]).Validate().IsSuccess);

    [Fact]
    public void UndefinedPropertyValue_Fails()
    {
        var props = new Dictionary<string, JsonElement> { ["x.y"] = default };
        AssertInvalid(Graph([Node("a", props)]).Validate(), "Nodes[0].Properties[\"x.y\"]");
    }

    [Fact]
    public void GraphAndEdgeProperties_AreChecked()
    {
        var bad = new Dictionary<string, JsonElement> { [".k"] = Json("1") };
        AssertInvalid((Graph([Node("a")]) with { Properties = bad }).Validate(), "Properties[\".k\"]");
        AssertInvalid(
            Graph([Node("a")], [new ReasonletEdge { From = "a", To = "a", Properties = bad }]).Validate(),
            "Edges[0].Properties[\".k\"]");
    }

    [Fact]
    public void PropertyKeyPath_IsEscaped()
    {
        var props = new Dictionary<string, JsonElement> { ["a\"b."] = Json("1") };
        AssertInvalid(Graph([Node("a", props)]).Validate(), "Nodes[0].Properties[\"a\\\"b.\"]");
    }

    [Fact]
    public void Depth32_Passes() =>
        Assert.True(Graph([Node("a", new Dictionary<string, JsonElement> { ["a.k"] = Nested(32) })]).Validate().IsSuccess);

    [Fact]
    public void Depth33_Fails() =>
        AssertInvalid(Graph([Node("a", new Dictionary<string, JsonElement> { ["a.k"] = Nested(33) })]).Validate(), "Nodes[0].Properties[\"a.k\"]");

    [Fact]
    public void ObjectNesting_CountsLikeArrays()
    {
        var obj32 = Json(string.Concat(Enumerable.Repeat("{\"a\":", 31)) + "1" + new string('}', 31));
        var obj33 = Json(string.Concat(Enumerable.Repeat("{\"a\":", 32)) + "1" + new string('}', 32));
        Assert.True(Graph([Node("a", new Dictionary<string, JsonElement> { ["a.k"] = obj32 })]).Validate().IsSuccess);
        Assert.True(Graph([Node("a", new Dictionary<string, JsonElement> { ["a.k"] = obj33 })]).Validate().IsFailure);
    }

    [Fact]
    public void TooManyNodes_Fails()
    {
        var nodes = Enumerable.Range(0, ReasonletGraphLimits.MaxNodes + 1).Select(i => Node($"n{i}")).ToArray();
        AssertInvalid(Graph(nodes).Validate(), "Nodes");
        Assert.True(Graph(nodes.Take(ReasonletGraphLimits.MaxNodes).ToArray()).Validate().IsSuccess);
    }

    [Fact]
    public void TooManyEdges_Fails()
    {
        var nodes = new[] { Node("a") };
        var edges = Enumerable.Range(0, ReasonletGraphLimits.MaxEdges + 1)
            .Select(i => new ReasonletEdge { From = "a", To = "a", Condition = $"c{i}" }).ToArray();
        AssertInvalid(Graph(nodes, edges).Validate(), "Edges");
        Assert.True(Graph(nodes, edges.Take(ReasonletGraphLimits.MaxEdges).ToArray()).Validate().IsSuccess);
    }

    /// <summary>A dictionary {"a.k": "xxx"} is <c>{"a.k":"xxx"}</c> in canonical form, 10 bytes more than the string.</summary>
    private static Dictionary<string, JsonElement> OfSize(int bytes) =>
        new() { ["a.k"] = Json("\"" + new string('x', bytes - 10) + "\"") };

    /// <summary>
    /// Like <see cref="OfSize"/>, but filled with as many U+1F600 as fit, 4 raw UTF-8 bytes each in canonical form,
    /// topped up with x. The relaxed encoder would have written each as a 12-byte escape pair.
    /// </summary>
    private static Dictionary<string, JsonElement> OfSizeAstral(int bytes)
    {
        var fill = bytes - 10;
        var text = string.Concat(Enumerable.Repeat("\U0001F600", fill / 4)) + new string('x', fill % 4);
        return new() { ["a.k"] = Json("\"" + text + "\"") };
    }

    [Fact]
    public void PropertiesSize_IsCanonicalUtf8()
    {
        Assert.Equal(0, CanonicalGraphWriter.PropertiesSize(null));
        Assert.Equal(0, CanonicalGraphWriter.PropertiesSize(new Dictionary<string, JsonElement>()));
        Assert.Equal(11, CanonicalGraphWriter.PropertiesSize(OfSize(11)));
        var props = new Dictionary<string, JsonElement> { ["a.k"] = Json("{ \"b\" : [ 1 , 2 ], \"a\" : \"\\u0041\" }") };
        Assert.Equal(Encoding.UTF8.GetByteCount("{\"a.k\":{\"a\":\"A\",\"b\":[1,2]}}"), CanonicalGraphWriter.PropertiesSize(props));
    }

    [Fact]
    public void PropertiesSize_CountsAstralCharacterAsFourBytes()
    {
        var props = new Dictionary<string, JsonElement> { ["a.k"] = Json("\"\\uD83D\\uDE00\"") };
        Assert.Equal(14, CanonicalGraphWriter.PropertiesSize(props));
    }

    [Fact]
    public void PropertiesAtLimit_Pass()
    {
        var graph = Graph([Node("a", OfSize(30000))]) with { Properties = OfSize(ReasonletGraphLimits.MaxPropertiesBytes - 30000) };
        Assert.True(graph.Validate().IsSuccess);
    }

    [Fact]
    public void PropertiesOverLimit_FailAcrossDictionaries()
    {
        var graph = Graph([Node("a", OfSize(30000))]) with { Properties = OfSize(ReasonletGraphLimits.MaxPropertiesBytes - 30000 + 1) };
        AssertInvalid(graph.Validate(), "Properties");
    }

    [Fact]
    public void AstralPropertiesAtLimit_Pass()
    {
        var props = OfSizeAstral(ReasonletGraphLimits.MaxPropertiesBytes);
        Assert.Equal(ReasonletGraphLimits.MaxPropertiesBytes, CanonicalGraphWriter.PropertiesSize(props));
        Assert.True(Graph([Node("a", props)]).Validate().IsSuccess);
    }

    [Fact]
    public void AstralPropertiesOverLimit_Fail() =>
        AssertInvalid(Graph([Node("a", OfSizeAstral(ReasonletGraphLimits.MaxPropertiesBytes + 1))]).Validate(), "Properties");

    [Fact]
    public void EdgePropertiesCountTowardLimit()
    {
        var graph = Graph(
            [Node("a")],
            [new ReasonletEdge { From = "a", To = "a", Properties = OfSize(ReasonletGraphLimits.MaxPropertiesBytes + 1) }]);
        AssertInvalid(graph.Validate(), "Properties");
    }

    [Fact]
    public void MultipleFailures_ReportFirstInOrder()
    {
        var graph = Graph([Node("a"), Node("a")], [new ReasonletEdge { From = "a", To = "zz" }]);
        AssertInvalid(graph.Validate(), "Nodes[1].Id");
    }

    [Fact]
    public void DisposedDocumentValue_FailsWithoutThrowing()
    {
        var doc = JsonDocument.Parse("1");
        var element = doc.RootElement;
        doc.Dispose();
        var props = new Dictionary<string, JsonElement> { ["a.k"] = element };
        AssertInvalid(Graph([Node("a", props)]).Validate(), "Nodes[0].Properties[\"a.k\"]");
    }

    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    [InlineData("\"\\uD800\\u0041\"")]
    [InlineData("\"\\uD800\\uD800\\uDC00\"")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("[{\"k\":[\"ok\",\"\\uDFFF\"]}]")]
    public void InvalidUtf16InPropertyValue_FailsWithoutThrowing(string json)
    {
        var props = new Dictionary<string, JsonElement> { ["a.k"] = Json(json) };
        AssertInvalid(Graph([Node("a", props)]).Validate(), "Nodes[0].Properties[\"a.k\"]");
    }

    [Theory]
    [InlineData("\"\\uD83D\\uDE00\"")]
    [InlineData("\"\\\\uD800\"")]
    [InlineData("\"\U0001F600 \\n \\u0041\"")]
    [InlineData("{\"\\uD83D\\uDE00\":[\"\\\"\"]}")]
    public void ValidUtf16InPropertyValue_Passes(string json)
    {
        var props = new Dictionary<string, JsonElement> { ["a.k"] = Json(json) };
        Assert.True(Graph([Node("a", props)]).Validate().IsSuccess);
    }

    /// <summary>
    /// <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/> does not validate UTF-8 inside strings,
    /// so a caller can build an element holding an encoded surrogate (<c>ED A0 80</c>) or a byte that never occurs in
    /// UTF-8 (<c>FF</c>). Writing it would silently replace it with U+FFFD.
    /// </summary>
    [Theory]
    [InlineData("22EDA08022")]
    [InlineData("22FF22")]
    public void InvalidUtf8InPropertyValue_Fails(string hex) => AssertInvalidUtf8(hex);

    [Theory]
    [InlineData("7B22EDA080223A317D")]
    [InlineData("7B22FF223A317D")]
    public void InvalidUtf8InPropertyMemberName_Fails(string hex) => AssertInvalidUtf8(hex);

    private static void AssertInvalidUtf8(string hex)
    {
        using var doc = JsonDocument.Parse(Convert.FromHexString(hex));
        var props = new Dictionary<string, JsonElement> { ["a.k"] = doc.RootElement.Clone() };
        AssertInvalid(Graph([Node("a", props)]).Validate(), "Nodes[0].Properties[\"a.k\"]");
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Type")]
    [InlineData("Name")]
    [InlineData("Tool")]
    public void LoneSurrogateInNodeField_Fails(string field)
    {
        var bad = "x\uD800";
        var node = field switch
        {
            "Id" => Node(bad),
            "Type" => Node("a") with { Type = bad },
            "Name" => Node("a") with { Name = bad },
            _ => Node("a") with { Tool = bad },
        };
        var r = Graph([node]).Validate();
        AssertInvalid(r, $"Nodes[0].{field}");
        Assert.Contains("valid UTF-16", r.Error.Message);
    }

    [Theory]
    [InlineData("From")]
    [InlineData("To")]
    [InlineData("Condition")]
    public void LoneSurrogateInEdgeField_Fails(string field)
    {
        var bad = "\uDC00x";
        var edge = new ReasonletEdge { From = "a", To = "a" };
        edge = field switch
        {
            "From" => edge with { From = bad },
            "To" => edge with { To = bad },
            _ => edge with { Condition = bad },
        };
        var r = Graph([Node("a")], [edge]).Validate();
        AssertInvalid(r, $"Edges[0].{field}");
        Assert.Contains("valid UTF-16", r.Error.Message);
    }

    [Fact]
    public void SurrogatePairsInGraphStrings_Pass()
    {
        var emoji = "\U0001F600";
        var graph = Graph(
            [new ReasonletNode { Id = emoji, Type = emoji, Name = emoji, Tool = emoji }],
            [new ReasonletEdge { From = emoji, To = emoji, Condition = emoji }]);
        Assert.True(graph.Validate().IsSuccess);
    }

    [Fact]
    public void InvalidUtf16InNodeId_ReportedBeforeLaterNodeChecks()
    {
        var graph = Graph([Node("a\uD800") with { Type = " " }, Node("b") with { Type = " " }]);
        AssertInvalid(graph.Validate(), "Nodes[0].Id");
    }

    [Fact]
    public void LoneSurrogateInPropertyKey_FailsWithoutThrowing()
    {
        var props = new Dictionary<string, JsonElement> { ["a.\uD800"] = Json("1") };
        AssertInvalid(Graph([Node("a", props)]).Validate(), "Nodes[0].Properties[\"a.\\uD800\"]");
    }

    [Theory]
    [InlineData("Name", "")]
    [InlineData("Name", " ")]
    [InlineData("Tool", "")]
    [InlineData("Tool", "\t")]
    public void BlankOptionalNodeField_Fails(string field, string blank)
    {
        var node = field == "Name" ? Node("a") with { Name = blank } : Node("a") with { Tool = blank };
        var r = Graph([node]).Validate();
        AssertInvalid(r, $"Nodes[0].{field}");
        Assert.Contains("must not be blank", r.Error.Message);
    }

    [Fact]
    public void BlankNodeName_ReportedBeforeBlankTool() =>
        AssertInvalid(Graph([Node("a") with { Name = " ", Tool = " " }]).Validate(), "Nodes[0].Name");

    [Fact]
    public void NonBlankOptionalNodeFields_Pass() =>
        Assert.True(Graph([Node("a") with { Name = "n", Tool = "t" }]).Validate().IsSuccess);

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1,"\u0061":2}""")]
    [InlineData("""{"x":[{"b":1}, {"b":2,"c":{"d":1,"d":1}}]}""")]
    [InlineData("""[{"k":true,"k":false}]""")]
    public void DuplicateMemberNameInPropertyValue_Fails(string json)
    {
        var props = new Dictionary<string, JsonElement> { ["a.k"] = Json(json) };
        var r = Graph([Node("a", props)]).Validate();
        AssertInvalid(r, "Nodes[0].Properties[\"a.k\"]");
        Assert.Contains("duplicate member name", r.Error.Message);
    }

    [Fact]
    public void SameMemberNameInSiblingObjects_Passes()
    {
        var props = new Dictionary<string, JsonElement> { ["a.k"] = Json("""{"x":{"a":1},"y":{"a":1},"a":[{"a":1},{"a":2}]}""") };
        Assert.True(Graph([Node("a", props)]).Validate().IsSuccess);
    }

    [Fact]
    public void KeyPath_EscapesAndHandlesNull()
    {
        Assert.Equal("P[null]", Check.KeyPath("P", null));
        Assert.Equal("P[\"a\\\\b\\u000A\"]", Check.KeyPath("P", "a\\b\n"));
        Assert.Equal("P[\"a\\uD800b\\uDC00\"]", Check.KeyPath("P", "a\uD800b\uDC00"));
        Assert.Equal("P[\"\U0001F600\"]", Check.KeyPath("P", "\U0001F600"));
    }

    [Fact]
    public void RepeatedKeyInCustomDictionary_Fails()
    {
        var repeated = new RepeatingDictionary(new("a.b", Json("1")), new("a.b", Json("2")));

        var r = (Graph([Node("a")]) with { Properties = repeated }).Validate();
        AssertInvalid(r, "Properties[\"a.b\"]");
        Assert.Contains("key must not repeat", r.Error.Message);
        AssertInvalid(Graph([Node("a", repeated)]).Validate(), "Nodes[0].Properties[\"a.b\"]");
        AssertInvalid(Graph([Node("a")], [new ReasonletEdge { From = "a", To = "a", Properties = repeated }]).Validate(), "Edges[0].Properties[\"a.b\"]");
    }

    [Fact]
    public void DistinctKeysInCustomDictionary_Pass() =>
        Assert.True((Graph([Node("a")]) with { Properties = new RepeatingDictionary(new("a.b", Json("1")), new("a.B", Json("2"))) }).Validate().IsSuccess);

    /// <summary>A dictionary that enumerates its entries as given, so it can yield one key twice.</summary>
    private sealed class RepeatingDictionary(params KeyValuePair<string, JsonElement>[] entries) : IReadOnlyDictionary<string, JsonElement>
    {
        public int Count => entries.Length;

        public IEnumerable<string> Keys => entries.Select(e => e.Key);

        public IEnumerable<JsonElement> Values => entries.Select(e => e.Value);

        public JsonElement this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out JsonElement value)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, JsonElement>>)entries).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
