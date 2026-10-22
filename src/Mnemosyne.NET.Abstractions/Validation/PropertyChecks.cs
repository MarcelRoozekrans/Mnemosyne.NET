using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Validation and measurement of free-form <c>Properties</c> dictionaries.</summary>
internal static class PropertyChecks
{
    private const string InvalidUtf16 = "value must contain only valid UTF-16 in its strings and member names";

    /// <summary>
    /// Checks the keys and values of a properties dictionary and returns the first failure.
    /// A key is two or more dot-separated segments, none of them empty, and valid UTF-16. A value must be defined,
    /// nested at most <see cref="ReasonletGraphLimits.MaxPropertyDepth"/> levels deep, and hold only valid UTF-16
    /// in its strings and member names, so that measuring and writing it cannot throw.
    /// </summary>
    public static UnitResult<MnemosyneError> Validate(IReadOnlyDictionary<string, JsonElement>? props, string pathPrefix)
    {
        if (props is null)
        {
            return UnitResult<MnemosyneError>.Success();
        }

        foreach (var entry in props)
        {
            var reason = FindProblem(entry.Key, entry.Value);
            if (reason is not null)
            {
                return Check.Fail(Check.KeyPath(pathPrefix, entry.Key), reason);
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    /// <summary>Returns the UTF-8 byte length of the compact JSON serialization, or 0 for null or empty.</summary>
    /// <remarks>Values must be defined. Written by hand with <see cref="Utf8JsonWriter"/> so no reflection is needed.</remarks>
    public static int SerializedSize(IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (props is null || props.Count == 0)
        {
            return 0;
        }

        var buffer = new ArrayBufferWriter<byte>();
        var options = new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            foreach (var entry in props)
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenCount;
    }

    private static string? FindProblem(string? key, JsonElement value)
    {
        if (!IsValidKey(key))
        {
            return "key must have at least two dot-separated segments, none of them empty";
        }

        if (!IsValidUtf16(key))
        {
            return "key must be valid UTF-16";
        }

        try
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                return "value must not be undefined";
            }

            return FindValueProblem(value, 1);
        }
        catch (ObjectDisposedException)
        {
            return "value belongs to a disposed JsonDocument";
        }
    }

    private static bool IsValidKey([NotNullWhen(true)] string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var start = 0;
        var segments = 0;
        while (true)
        {
            var dot = key.IndexOf('.', start);
            var end = dot < 0 ? key.Length : dot;
            if (end == start)
            {
                return false;
            }

            segments++;
            if (dot < 0)
            {
                return segments >= 2;
            }

            start = dot + 1;
        }
    }

    /// <summary>Walks a value once, checking its depth and that every string and member name is valid UTF-16.</summary>
    private static string? FindValueProblem(JsonElement element, int level)
    {
        if (level > ReasonletGraphLimits.MaxPropertyDepth)
        {
            return $"value must not be nested deeper than {ReasonletGraphLimits.MaxPropertyDepth} levels";
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return HasValidEscapes(JsonMarshal.GetRawUtf8Value(element)) ? null : InvalidUtf16;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindValueProblem(item, level + 1) is { } problem)
                    {
                        return problem;
                    }
                }

                return null;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (!HasValidEscapes(JsonMarshal.GetRawUtf8PropertyName(member)))
                    {
                        return InvalidUtf16;
                    }

                    if (FindValueProblem(member.Value, level + 1) is { } problem)
                    {
                        return problem;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Returns whether a string has no lone surrogate.</summary>
    private static bool IsValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether raw, still-escaped JSON string text decodes to valid UTF-16.
    /// </summary>
    /// <remarks>
    /// The parser has already checked the UTF-8 bytes and the escape syntax, but it keeps <c>\uXXXX</c> escapes
    /// undecoded, so a lone surrogate written as an escape survives until something unescapes the text.
    /// Every high surrogate escape must be followed directly by a low surrogate escape, and every low surrogate
    /// escape must follow a high one.
    /// </remarks>
    private static bool HasValidEscapes(ReadOnlySpan<byte> raw)
    {
        var expectLow = false;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\')
            {
                if (expectLow)
                {
                    return false;
                }

                continue;
            }

            if (i + 1 >= raw.Length)
            {
                return false;
            }

            if (raw[i + 1] != (byte)'u')
            {
                if (expectLow)
                {
                    return false;
                }

                i++;
                continue;
            }

            if (i + 6 > raw.Length
                || !ushort.TryParse(raw.Slice(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit))
            {
                return false;
            }

            i += 5;
            var c = (char)unit;
            if (char.IsLowSurrogate(c) != expectLow)
            {
                return false;
            }

            expectLow = char.IsHighSurrogate(c);
        }

        return !expectLow;
    }
}
