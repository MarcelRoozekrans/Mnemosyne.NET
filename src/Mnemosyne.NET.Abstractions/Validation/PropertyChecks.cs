using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Validation and measurement of free-form <c>Properties</c> dictionaries.</summary>
internal static class PropertyChecks
{
    /// <summary>
    /// Checks the keys and values of a properties dictionary and returns the first failure.
    /// A key is one or more dot-separated segments, none of them empty. A value must be defined
    /// and nested at most <see cref="ReasonletGraphLimits.MaxPropertyDepth"/> levels deep.
    /// </summary>
    public static UnitResult<MnemosyneError> Validate(IReadOnlyDictionary<string, JsonElement>? props, string pathPrefix)
    {
        if (props is null)
        {
            return UnitResult<MnemosyneError>.Success();
        }

        foreach (var entry in props)
        {
            var path = Check.KeyPath(pathPrefix, entry.Key);
            if (!IsValidKey(entry.Key))
            {
                return Check.Fail(path, "key must be dot-separated segments that are not empty");
            }

            if (entry.Value.ValueKind == JsonValueKind.Undefined)
            {
                return Check.Fail(path, "value must not be undefined");
            }

            if (!FitsDepth(entry.Value, 1))
            {
                return Check.Fail(path, $"value must not be nested deeper than {ReasonletGraphLimits.MaxPropertyDepth} levels");
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

    private static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var start = 0;
        while (true)
        {
            var dot = key.IndexOf('.', start);
            var end = dot < 0 ? key.Length : dot;
            if (end == start)
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            start = dot + 1;
        }
    }

    private static bool FitsDepth(JsonElement element, int level)
    {
        if (level > ReasonletGraphLimits.MaxPropertyDepth)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!FitsDepth(item, level + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (!FitsDepth(member.Value, level + 1))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }
}
