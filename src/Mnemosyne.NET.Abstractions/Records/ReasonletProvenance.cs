using System.Globalization;
using System.Text;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Records where a Reasonlet came from.</summary>
/// <param name="Source">The originating source. Must not be blank.</param>
/// <param name="RunId">An optional identifier of the originating run.</param>
/// <param name="SubjectId">An optional identifier of the subject the Reasonlet is about.</param>
/// <param name="Tags">Optional free-form tags. Keys must not be blank and values must not be null; blank values are allowed.</param>
public sealed record ReasonletProvenance(
    string Source,
    string? RunId,
    string? SubjectId,
    IReadOnlyDictionary<string, string>? Tags)
{
    /// <summary>Validates the provenance.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var source = Check.NotBlank(Source, nameof(Source));
        if (source.IsFailure)
        {
            return source;
        }

        if (Tags is null)
        {
            return UnitResult<MnemosyneError>.Success();
        }

        foreach (var tag in Tags)
        {
            var path = tag.Key is null ? "Tags[null]" : $"Tags[\"{EscapeKey(tag.Key)}\"]";
            var result = Check.All(
                () => Check.NotBlank(tag.Key, path),
                () => Check.NotNull(tag.Value, path));
            if (result.IsFailure)
            {
                return result;
            }
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private static string EscapeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c == '\\')
            {
                builder.Append("\\\\");
            }
            else if (c == '"')
            {
                builder.Append("\\\"");
            }
            else if (char.IsControl(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
