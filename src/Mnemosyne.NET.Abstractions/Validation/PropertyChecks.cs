using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Unicode;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Validation of free-form <c>Properties</c> dictionaries. <see cref="CanonicalGraphWriter.PropertiesSize"/> measures them.</summary>
internal static class PropertyChecks
{
    private const string InvalidText = "value must contain only valid UTF-8 and UTF-16 in its strings and member names";

    /// <summary>
    /// Checks the keys and values of a properties dictionary and returns the first failure.
    /// A key is two or more dot-separated segments, none of them empty, and valid UTF-16. No key may repeat (ordinal),
    /// which a custom <see cref="IReadOnlyDictionary{TKey, TValue}"/> could otherwise do, so the canonical form is
    /// unambiguous. A value must be defined, nested at most <see cref="ReasonletGraphLimits.MaxPropertyDepth"/> levels
    /// deep, hold only valid UTF-8 and UTF-16 in its strings and member names, so that measuring and writing it cannot
    /// throw, and have no duplicate member names in any object, so that its canonical form is unambiguous.
    /// </summary>
    public static UnitResult<MnemosyneError> Validate(IReadOnlyDictionary<string, JsonElement>? props, string pathPrefix)
    {
        if (props is null)
        {
            return UnitResult<MnemosyneError>.Success();
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in props)
        {
            var reason = FindProblem(entry.Key, entry.Value)
                ?? (keys.Add(entry.Key) ? null : "key must not repeat in the same dictionary");
            if (reason is not null)
            {
                return Check.Fail(Check.KeyPath(pathPrefix, entry.Key), reason);
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private static string? FindProblem(string? key, JsonElement value)
    {
        if (!IsValidKey(key))
        {
            return "key must have at least two dot-separated segments, none of them empty";
        }

        if (!Check.IsValidUtf16(key))
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

    /// <summary>
    /// Walks a value once, checking its depth, that every string and member name is valid UTF-8 and UTF-16, and that
    /// no object repeats a member name. Names are compared after unescaping, so <c>"a"</c> and <c>"\u0061"</c> clash.
    /// </summary>
    private static string? FindValueProblem(JsonElement element, int level)
    {
        if (level > ReasonletGraphLimits.MaxPropertyDepth)
        {
            return $"value must not be nested deeper than {ReasonletGraphLimits.MaxPropertyDepth} levels";
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return IsValidText(JsonMarshal.GetRawUtf8Value(element)) ? null : InvalidText;
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
                HashSet<string>? names = null;
                foreach (var member in element.EnumerateObject())
                {
                    if (!IsValidText(JsonMarshal.GetRawUtf8PropertyName(member)))
                    {
                        return InvalidText;
                    }

                    names ??= new HashSet<string>(StringComparer.Ordinal);
                    if (!names.Add(member.Name))
                    {
                        return "value must not contain a duplicate member name in any object";
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

    /// <summary>
    /// Returns whether raw, still-escaped JSON string text is valid UTF-8 and decodes to valid UTF-16.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The UTF-8 is checked here because <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>
    /// does not validate the bytes inside strings and member names. A caller-built element can hold an encoded
    /// surrogate or a byte such as <c>FF</c>, which writing would silently replace with U+FFFD.
    /// </para>
    /// <para>
    /// The parser does check the escape syntax, but it keeps <c>\uXXXX</c> escapes undecoded, so a lone surrogate
    /// written as an escape survives until something unescapes the text.
    /// </para>
    /// </remarks>
    private static bool IsValidText(ReadOnlySpan<byte> raw) => Utf8.IsValid(raw) && HasValidEscapes(raw);

    /// <summary>
    /// Returns whether the <c>\uXXXX</c> escapes in raw JSON string text decode to valid UTF-16: every high surrogate
    /// escape is followed directly by a low surrogate escape, and every low surrogate escape follows a high one.
    /// </summary>
    /// <remarks>
    /// The parser checks the escape syntax, so text from a <see cref="JsonElement"/> is always well formed. Malformed
    /// syntax, such as a trailing backslash or a short <c>\u</c> escape, still returns <see langword="false"/> rather
    /// than reading past the end.
    /// </remarks>
    internal static bool HasValidEscapes(ReadOnlySpan<byte> raw)
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
