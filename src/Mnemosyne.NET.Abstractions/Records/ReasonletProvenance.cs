using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Records where a Reasonlet came from.</summary>
/// <param name="Source">The originating source. Must not be blank.</param>
/// <param name="RunId">An optional identifier of the originating run.</param>
/// <param name="SubjectId">An optional identifier of the subject the Reasonlet is about.</param>
/// <param name="Tags">Optional free-form tags. Keys must not be blank.</param>
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

        var index = 0;
        foreach (var key in Tags.Keys)
        {
            var result = Check.NotBlank(key, $"Tags[{index}]");
            if (result.IsFailure)
            {
                return result;
            }

            index++;
        }

        return UnitResult<MnemosyneError>.Success();
    }
}
