using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Writes the canonical form of a <see cref="ReasonletGraph"/>, the input of its content hash.</summary>
/// <remarks>
/// <para>
/// The canonical form is a permanent contract: stored hashes depend on it, so any change splits every stored procedure.
/// It is compact UTF-8 JSON with these rules:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Every object's members are in .NET ordinal order, comparing UTF-16 code units, including nested property values.
/// </description></item>
/// <item><description>Nodes are sorted by id. Edges are sorted by from, then to, then condition, with null first.</description></item>
/// <item><description>Arrays inside property values keep their order.</description></item>
/// <item><description>Null members, and null or empty <c>properties</c>, are left out.</description></item>
/// <item><description>Numbers are written as their raw text, so <c>1</c> and <c>1.0</c> differ.</description></item>
/// <item><description>
/// Strings and member names are unescaped, then written with the fixed RFC 8785 (JCS) escape set: only <c>"</c>,
/// <c>\</c> and U+0000 to U+001F are escaped, as <c>\b \f \n \r \t</c> where those exist and as <c>\u00xx</c> with
/// lowercase hex otherwise. Everything else, including supplementary characters, is written as raw UTF-8.
/// </description></item>
/// </list>
/// <para>
/// The JSON is emitted by hand rather than through <see cref="Utf8JsonWriter"/> and a <c>JavaScriptEncoder</c>,
/// because those encoders escape code points by the runtime's Unicode tables, and a runtime upgrade would then change
/// the bytes. Nothing here consults a Unicode table or the current culture.
/// </para>
/// <para>
/// The writer does not validate. Callers validate first, because invalid text or an undefined value would throw or
/// be written wrongly.
/// </para>
/// </remarks>
internal static class CanonicalGraphWriter
{
    /// <summary>Returns the lowercase hex SHA-256 of the canonical form.</summary>
    /// <param name="graph">A graph that has passed <see cref="ReasonletGraph.Validate"/>.</param>
    /// <returns>64 lowercase hex characters.</returns>
    public static string Hash(ReasonletGraph graph) => Convert.ToHexStringLower(SHA256.HashData(Write(graph)));

    /// <summary>Returns the canonical form as UTF-8 bytes.</summary>
    /// <param name="graph">A graph that has passed <see cref="ReasonletGraph.Validate"/>.</param>
    /// <returns>The canonical JSON.</returns>
    public static byte[] Write(ReasonletGraph graph)
    {
        var output = new ArrayBufferWriter<byte>();

        output.Write("{\"edges\":["u8);
        var first = true;
        foreach (var edge in graph.Edges
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Condition, StringComparer.Ordinal))
        {
            Separator(output, ref first);
            WriteEdge(output, edge);
        }

        output.Write("],\"nodes\":["u8);
        first = true;
        foreach (var node in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            Separator(output, ref first);
            WriteNode(output, node);
        }

        output.Write("]"u8);
        WriteProperties(output, graph.Properties);
        output.Write(",\"schemaVersion\":"u8);
        WriteInteger(output, graph.SchemaVersion);
        output.Write("}"u8);

        return output.WrittenSpan.ToArray();
    }

    private static void WriteEdge(ArrayBufferWriter<byte> output, ReasonletEdge edge)
    {
        output.Write("{"u8);
        if (edge.Condition is not null)
        {
            output.Write("\"condition\":"u8);
            WriteString(output, edge.Condition);
            output.Write(","u8);
        }

        output.Write("\"from\":"u8);
        WriteString(output, edge.From);
        WriteProperties(output, edge.Properties);
        output.Write(",\"to\":"u8);
        WriteString(output, edge.To);
        output.Write("}"u8);
    }

    private static void WriteNode(ArrayBufferWriter<byte> output, ReasonletNode node)
    {
        output.Write("{\"id\":"u8);
        WriteString(output, node.Id);
        if (node.Name is not null)
        {
            output.Write(",\"name\":"u8);
            WriteString(output, node.Name);
        }

        WriteProperties(output, node.Properties);
        if (node.Tool is not null)
        {
            output.Write(",\"tool\":"u8);
            WriteString(output, node.Tool);
        }

        output.Write(",\"type\":"u8);
        WriteString(output, node.Type);
        output.Write("}"u8);
    }

    /// <summary>
    /// Writes <c>,"properties":{…}</c> with the keys in ordinal order, or nothing when null or empty.
    /// It is never the first member of its object, so it always starts with a comma.
    /// </summary>
    private static void WriteProperties(ArrayBufferWriter<byte> output, IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (props is null || props.Count == 0)
        {
            return;
        }

        output.Write(",\"properties\":{"u8);
        var first = true;
        foreach (var entry in props.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Separator(output, ref first);
            WriteString(output, entry.Key);
            output.Write(":"u8);
            WriteValue(output, entry.Value);
        }

        output.Write("}"u8);
    }

    private static void WriteValue(ArrayBufferWriter<byte> output, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.Write("{"u8);
                var firstMember = true;

                // Validation rejects duplicate member names, so this order is total.
                foreach (var member in element.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    Separator(output, ref firstMember);
                    WriteString(output, member.Name);
                    output.Write(":"u8);
                    WriteValue(output, member.Value);
                }

                output.Write("}"u8);
                break;
            case JsonValueKind.Array:
                output.Write("["u8);
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    Separator(output, ref firstItem);
                    WriteValue(output, item);
                }

                output.Write("]"u8);
                break;
            case JsonValueKind.String:
                WriteString(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                output.Write(JsonMarshal.GetRawUtf8Value(element));
                break;
            case JsonValueKind.True:
                output.Write("true"u8);
                break;
            case JsonValueKind.False:
                output.Write("false"u8);
                break;
            case JsonValueKind.Null:
                output.Write("null"u8);
                break;
            default:
                throw new ArgumentException($"cannot write a {element.ValueKind} value", nameof(element));
        }
    }

    /// <summary>Writes a JSON string with the RFC 8785 escape set. The text must be valid UTF-16.</summary>
    private static void WriteString(ArrayBufferWriter<byte> output, string value)
    {
        output.Write("\""u8);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    output.Write("\\\""u8);
                    break;
                case '\\':
                    output.Write("\\\\"u8);
                    break;
                case '\b':
                    output.Write("\\b"u8);
                    break;
                case '\f':
                    output.Write("\\f"u8);
                    break;
                case '\n':
                    output.Write("\\n"u8);
                    break;
                case '\r':
                    output.Write("\\r"u8);
                    break;
                case '\t':
                    output.Write("\\t"u8);
                    break;
                case < 0x20:
                    ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
                    output.Write("\\u00"u8);
                    utf8[0] = hex[rune.Value >> 4];
                    utf8[1] = hex[rune.Value & 0xF];
                    output.Write(utf8[..2]);
                    break;
                default:
                    output.Write(utf8[..rune.EncodeToUtf8(utf8)]);
                    break;
            }
        }

        output.Write("\""u8);
    }

    private static void WriteInteger(ArrayBufferWriter<byte> output, int value)
    {
        Span<byte> digits = stackalloc byte[11];
        Utf8Formatter.TryFormat(value, digits, out var written);
        output.Write(digits[..written]);
    }

    private static void Separator(ArrayBufferWriter<byte> output, ref bool first)
    {
        if (!first)
        {
            output.Write(","u8);
        }

        first = false;
    }
}
