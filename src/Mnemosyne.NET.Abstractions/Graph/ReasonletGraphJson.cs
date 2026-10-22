using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Reads and writes <see cref="ReasonletGraph"/> as JSON without reflection, so it is safe for Native AOT.</summary>
/// <remarks>
/// The JSON uses camelCase member names and leaves out null members and null or empty <c>properties</c>.
/// Strings are written with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>, the same encoder the
/// <see cref="ReasonletGraphLimits.MaxPropertiesBytes"/> limit measures with. The output is meant to be stored and
/// hashed, not embedded in HTML.
/// </remarks>
public static class ReasonletGraphJson
{
    /// <summary>
    /// The maximum nesting depth of a graph document: <see cref="ReasonletGraphLimits.MaxPropertyDepth"/> plus the
    /// four levels a property value can sit under (root object, <c>edges</c> array, edge object, <c>properties</c> object).
    /// </summary>
    /// <remarks>
    /// Any graph that passes <see cref="ReasonletGraph.Validate"/> fits, and a value one level too deep still parses,
    /// so <see cref="ReasonletGraph.Validate"/> reports it with its property path. Anything deeper is rejected while
    /// reading, without recursing further.
    /// </remarks>
    internal const int MaxDepth = ReasonletGraphLimits.MaxPropertyDepth + 4;

    private static readonly JsonTypeInfo<ReasonletGraph> GraphInfo = CreateGraphInfo();

    /// <summary>Serializes a graph to compact JSON.</summary>
    /// <param name="graph">The graph to write. It is not validated.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
    public static string Serialize(ReasonletGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph, GraphInfo);
    }

    /// <summary>Deserializes and validates a graph. Never throws on bad input.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>
    /// The graph, or a <see cref="MnemosyneErrorCode.Validation"/> failure when the text is not valid UTF-16 or JSON, does not
    /// match the schema, has duplicate or unknown members, nests too deep, or fails
    /// <see cref="ReasonletGraph.Validate"/>.
    /// </returns>
    public static Result<ReasonletGraph, MnemosyneError> Deserialize(string json)
    {
        if (json is null)
        {
            return Fail("json", "must not be null");
        }

        ReasonletGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize(json, GraphInfo);
        }
        catch (JsonException ex)
        {
            return Fail(ex.Path ?? "$", ex.Message);
        }
        catch (ArgumentException)
        {
            // Thrown before parsing when the text holds a lone surrogate and cannot be transcoded to UTF-8.
            return Fail("$", "text is not valid UTF-16");
        }

        if (graph is null)
        {
            return Fail("$", "must not be null");
        }

        var valid = graph.Validate();
        return valid.IsSuccess
            ? Result<ReasonletGraph, MnemosyneError>.Success(graph)
            : Result<ReasonletGraph, MnemosyneError>.Failure(valid.Error);
    }

    private static Result<ReasonletGraph, MnemosyneError> Fail(string path, string reason) =>
        Result<ReasonletGraph, MnemosyneError>.Failure(Check.Fail(path, reason).Error);

    private static JsonTypeInfo<ReasonletGraph> CreateGraphInfo()
    {
        var options = new JsonSerializerOptions(ReasonletGraphJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = ReasonletGraphJsonContext.Default.WithAddedModifier(OmitEmptyProperties),
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<ReasonletGraph>)options.GetTypeInfo(typeof(ReasonletGraph));
    }

    /// <summary>Skips writing a <c>Properties</c> dictionary that is empty; null is already skipped by the options.</summary>
    private static void OmitEmptyProperties(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.PropertyType == typeof(IReadOnlyDictionary<string, JsonElement>))
            {
                property.ShouldSerialize = static (_, value) => value is IReadOnlyCollection<KeyValuePair<string, JsonElement>> { Count: > 0 };
            }
        }
    }
}
