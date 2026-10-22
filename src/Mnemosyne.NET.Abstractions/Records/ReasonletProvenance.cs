using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Records where a Reasonlet, an execution or a recall came from.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletProvenance
{
    /// <summary>Gets the originating source. Must not be blank.</summary>
    public required string Source { get; init; }

    /// <summary>Gets an optional identifier of the originating run.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets an optional identifier of the subject the record is about, so it can be erased by subject.</summary>
    public string? SubjectId { get; init; }

    /// <summary>Gets optional free-form tags. Keys must not be blank and values must not be null; blank values are allowed.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

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
            var path = Check.KeyPath(nameof(Tags), tag.Key);
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
}
