using System.Buffers;
using System.Text;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Abstractions;

/// <summary>
/// Branches of the canonical writer, the escape check and the reader error path that the public API cannot reach,
/// because validation or the JSON parser rules their inputs out first. They are tested directly so the guards stay
/// correct.
/// </summary>
public class CanonicalEdgeCaseTests
{
    [Theory]
    [InlineData("false", "false")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    public void WriteValue_WritesLiterals(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        var output = new ArrayBufferWriter<byte>();

        CanonicalGraphWriter.WriteValue(output, doc.RootElement);

        Assert.Equal(expected, Encoding.UTF8.GetString(output.WrittenSpan));
    }

    [Fact]
    public void WriteValue_UndefinedElement_Throws()
    {
        var output = new ArrayBufferWriter<byte>();

        Assert.Throws<ArgumentException>(() => CanonicalGraphWriter.WriteValue(output, default));
    }

    [Theory]
    [InlineData("abc\\")]
    [InlineData("\\u12")]
    [InlineData("\\uZZZZ")]
    public void HasValidEscapes_MalformedSyntax_ReturnsFalse(string raw)
    {
        Assert.False(PropertyChecks.HasValidEscapes(Encoding.UTF8.GetBytes(raw)));
    }

    [Theory]
    [InlineData("\\uD800\\n", false)]
    [InlineData("\\uD83D\\uDE00\\n", true)]
    [InlineData("\\n\\t", true)]
    public void HasValidEscapes_HighSurrogateNeedsLowEscapeNext(string raw, bool expected)
    {
        Assert.Equal(expected, PropertyChecks.HasValidEscapes(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public void ErrorPath_FallsBackToRootWithoutAPath()
    {
        Assert.Equal("$", ReasonletGraphJson.ErrorPath(new JsonException("no path")));
        Assert.Equal("$.nodes[0]", ReasonletGraphJson.ErrorPath(new JsonException("x", "$.nodes[0]", null, null)));
    }

    [Fact]
    public void FalseAndTruePropertyValues_HashDifferently()
    {
        var withFalse = TestData.Graph() with { Properties = Props("false") };
        var withTrue = TestData.Graph() with { Properties = Props("true") };
        var withText = TestData.Graph() with { Properties = Props("\"false\"") };

        var hashFalse = withFalse.ComputeContentHash().Value;

        Assert.NotEqual(hashFalse, withTrue.ComputeContentHash().Value);
        Assert.NotEqual(hashFalse, withText.ComputeContentHash().Value);
    }

    private static Dictionary<string, JsonElement> Props(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement> { ["a.flag"] = doc.RootElement.Clone() };
    }
}
