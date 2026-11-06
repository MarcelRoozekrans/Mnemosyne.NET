using System.Text.Json;
using Json.Schema;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Graph;

/// <summary>
/// Pins graph schema v1. The golden documents are frozen: a change that makes one of them fail is a breaking change,
/// which the compatibility policy only allows as schema version 2.
/// </summary>
public class GraphCompatibilityTests
{
    // Its own registry: GraphSchemaTests registers the same $id in the global one, and a second registration throws.
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", "reasonlet-graph.v1.schema.json")),
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    public static TheoryData<string> GoldenFiles => ["minimal.json", "full.json", "max-size.json"];

    private static string Golden(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Graph", "GoldenV1", file));

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void EveryGoldenV1DocumentDeserializesAndRoundTrips(string file)
    {
        var first = ReasonletGraphJson.Deserialize(Golden(file));
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        var written = ReasonletGraphJson.Serialize(first.Value);
        var second = ReasonletGraphJson.Deserialize(written);
        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);

        Assert.Equal(first.Value.ComputeContentHash().Value, second.Value.ComputeContentHash().Value);
        Assert.Equal(written, ReasonletGraphJson.Serialize(second.Value));
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void EveryGoldenV1DocumentValidatesAgainstTheSchema(string file)
    {
        using var doc = JsonDocument.Parse(Golden(file));

        Assert.True(Schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);
    }

    [Theory]
    [InlineData("minimal.json", "2e1a5a8ad16b620f74490a495289115d11321bd34444226dece49973b5cee88f")]
    [InlineData("full.json", "5226fdaa10ec84857acd33e2579d481d2ba5adc01034662728718bf5837a27cd")]
    [InlineData("max-size.json", "2836be354ae4aff27500def814dc2c44ce1689f55fbb21cd27cfe6075af34356")]
    public void TheContentHashOfAGoldenV1DocumentNeverChanges(string file, string expected)
    {
        var graph = ReasonletGraphJson.Deserialize(Golden(file));

        Assert.True(graph.IsSuccess);
        Assert.Equal(expected, graph.Value.ComputeContentHash().Value);
    }

    [Fact]
    public void ADocumentWithSchemaVersionTwoIsRejectedAsUnsupported()
    {
        var result = ReasonletGraphJson.Deserialize("""{"schemaVersion":2,"nodes":[{"id":"a","type":"t"}],"edges":[]}""");

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Contains("unsupported schema version 2", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithAnUnknownTopLevelMemberIsRejected()
    {
        var result = ReasonletGraphJson.Deserialize(
            """{"schemaVersion":1,"nodes":[{"id":"a","type":"t"}],"edges":[],"futureMember":true}""");

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
    }
}
