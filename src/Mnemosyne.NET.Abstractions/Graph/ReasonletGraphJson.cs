using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Reads and writes <see cref="ReasonletGraph"/> as JSON without reflection, so it is safe for Native AOT.</summary>
/// <remarks>
/// The JSON uses camelCase member names and leaves out null members and null or empty <c>properties</c>.
/// Strings are written with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>. The output is meant to be stored,
/// not embedded in HTML. <see cref="ReasonletGraph.ComputeContentHash"/> does not hash this text, and the
/// <see cref="ReasonletGraphLimits.MaxPropertiesBytes"/> limit does not measure it: both use the canonical form with its
/// fixed RFC 8785 escape set.
/// </remarks>
public static class ReasonletGraphJson
{
    /// <summary>
    /// The maximum nesting depth of a graph document: <see cref="ReasonletGraphLimits.MaxPropertyDepth"/> plus the
    /// four levels a property value can sit under (root object, <c>edges</c> array, edge object, <c>properties</c> object).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any graph that passes <see cref="ReasonletGraph.Validate"/> fits. A property value one level too deep is
    /// rejected either by the reader, with a JSON path, or by <see cref="ReasonletGraph.Validate"/>, with a property
    /// path, depending on its innermost value. The reader counts only arrays and objects, so an innermost scalar keeps
    /// the document within this depth and <see cref="ReasonletGraph.Validate"/> reports it, while an innermost empty
    /// array or object adds a reader level and the reader reports it.
    /// </para>
    /// <para>Anything deeper is rejected while reading, without recursing further.</para>
    /// </remarks>
    internal const int MaxDepth = ReasonletGraphLimits.MaxPropertyDepth + 4;

    private static readonly JsonTypeInfo<ReasonletGraph> GraphInfo = CreateGraphInfo();

    /// <summary>Transcodes the input text, throwing on a lone surrogate instead of replacing it.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The namespace prefix stripped from type names in JSON error messages.</summary>
    private static readonly string NamespacePrefix = typeof(ReasonletGraph).Namespace + ".";

    /// <summary>Serializes a graph to compact JSON.</summary>
    /// <param name="graph">The graph to write. It is not validated, so call <see cref="ReasonletGraph.Validate"/> first.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A property value is <c>default(JsonElement)</c>.</exception>
    /// <exception cref="JsonException">
    /// The graph nests deeper than the serializer's depth limit, which a property value past
    /// <see cref="ReasonletGraphLimits.MaxPropertyDepth"/> can do.
    /// </exception>
    /// <remarks>
    /// A graph that passes <see cref="ReasonletGraph.Validate"/> never throws here.
    /// </remarks>
    public static string Serialize(ReasonletGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph, GraphInfo);
    }

    /// <summary>Deserializes and validates a graph. Never throws on bad input.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>
    /// The graph, or a <see cref="MnemosyneErrorCode.Validation"/> failure when the text is not valid UTF-16 or JSON, does not
    /// match the schema, has duplicate or unknown members, nests too deep, or fails <see cref="ReasonletGraph.Validate"/>.
    /// </returns>
    /// <remarks>
    /// Messages read "path: reason", in one of two path styles:
    /// <list type="bullet">
    /// <item><description>
    /// Errors found while reading the JSON happen before a graph exists, so their path is a JSON path into the text,
    /// such as <c>$.nodes[0].colour</c>, or <c>$</c> when the reader has no position.
    /// </description></item>
    /// <item><description>
    /// Errors from <see cref="ReasonletGraph.Validate"/> use member paths on the graph, such as <c>Edges[0].To</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static Result<ReasonletGraph, MnemosyneError> Deserialize(string json)
    {
        if (json is null)
        {
            return Fail("json", "must not be null");
        }

        int length;
        try
        {
            length = StrictUtf8.GetByteCount(json);
        }
        catch (EncoderFallbackException)
        {
            return Fail("$", "text is not valid UTF-16");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        ReasonletGraph? graph;
        try
        {
            var utf8 = buffer.AsSpan(0, StrictUtf8.GetBytes(json, buffer));
            graph = JsonSerializer.Deserialize(utf8, GraphInfo);
        }
        catch (JsonException ex)
        {
            return Fail(ErrorPath(ex), Reason(ex.Message));
        }
        finally
        {
            // Property values are copied into their own documents while reading, so nothing aliases the buffer.
            // It is cleared because graphs are user data.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
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

    /// <summary>The JSON path of a reader error, or the root <c>$</c> when the exception carries none.</summary>
    internal static string ErrorPath(JsonException ex) => ex.Path ?? "$";

    /// <summary>Cuts the reader's " Path: … | LineNumber: …" suffix, which repeats the error path, and shortens type names.</summary>
    private static string Reason(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        var reason = cut < 0 ? message : message[..cut];
        return reason.Replace(NamespacePrefix, string.Empty, StringComparison.Ordinal);
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
