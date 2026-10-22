using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Writes the canonical form of a <see cref="ReasonletGraph"/>, the input of its content hash.</summary>
/// <remarks>
/// <para>
/// The canonical form is a permanent contract: stored hashes depend on it, so any change splits every stored procedure.
/// It is compact UTF-8 JSON with these rules:
/// </para>
/// <list type="bullet">
/// <item><description>Every object's members are in ordinal order, including nested property values.</description></item>
/// <item><description>Nodes are sorted by id. Edges are sorted by from, then to, then condition, with null first.</description></item>
/// <item><description>Arrays inside property values keep their order.</description></item>
/// <item><description>Null members, and null or empty <c>properties</c>, are left out.</description></item>
/// <item><description>Numbers are written as their raw text, so <c>1</c> and <c>1.0</c> differ.</description></item>
/// <item><description>
/// Strings and member names are unescaped, then written with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>,
/// the same encoder <see cref="ReasonletGraphJson"/> uses.
/// </description></item>
/// </list>
/// <para>
/// The writer does not validate. Callers validate first, because invalid text or an undefined value would throw.
/// </para>
/// </remarks>
internal static class CanonicalGraphWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Returns the lowercase hex SHA-256 of the canonical form.</summary>
    /// <param name="graph">A graph that has passed <see cref="ReasonletGraph.Validate"/>.</param>
    /// <returns>64 lowercase hex characters.</returns>
    public static string Hash(ReasonletGraph graph) => Convert.ToHexStringLower(SHA256.HashData(Write(graph)));

    /// <summary>Returns the canonical form as UTF-8 bytes.</summary>
    /// <param name="graph">A graph that has passed <see cref="ReasonletGraph.Validate"/>.</param>
    /// <returns>The canonical JSON.</returns>
    public static byte[] Write(ReasonletGraph graph)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("edges");
            foreach (var edge in graph.Edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Condition, StringComparer.Ordinal))
            {
                WriteEdge(writer, edge);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("nodes");
            foreach (var node in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                WriteNode(writer, node);
            }

            writer.WriteEndArray();

            WriteProperties(writer, graph.Properties);
            writer.WriteNumber("schemaVersion", graph.SchemaVersion);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteEdge(Utf8JsonWriter writer, ReasonletEdge edge)
    {
        writer.WriteStartObject();
        WriteOptional(writer, "condition", edge.Condition);
        writer.WriteString("from", edge.From);
        WriteProperties(writer, edge.Properties);
        writer.WriteString("to", edge.To);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, ReasonletNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("id", node.Id);
        WriteOptional(writer, "name", node.Name);
        WriteProperties(writer, node.Properties);
        WriteOptional(writer, "tool", node.Tool);
        writer.WriteString("type", node.Type);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>Writes a <c>properties</c> member with its keys in ordinal order, or nothing when null or empty.</summary>
    private static void WriteProperties(Utf8JsonWriter writer, IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (props is null || props.Count == 0)
        {
            return;
        }

        writer.WriteStartObject("properties");
        foreach (var entry in props.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(entry.Key);
            WriteValue(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                // OrderBy is stable, so duplicate member names keep their document order.
                foreach (var member in element.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(member.Name);
                    WriteValue(writer, member.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException($"cannot write a {element.ValueKind} value", nameof(element));
        }
    }
}
