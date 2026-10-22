using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A request for the best matching Reasonlets.</summary>
/// <param name="Scope">The scope to search.</param>
/// <param name="Question">The question to match. Must not be blank.</param>
/// <param name="Intent">An optional intent filter. When present it must not be blank.</param>
/// <param name="Top">The maximum number of matches, from 1 to 50. Defaults to 3.</param>
public sealed record ReasonletQuery(ReasonletScope Scope, string Question, string? Intent, int Top = 3)
{
    /// <summary>Validates the query.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => Check.NotBlank(Question, nameof(Question)),
        () => Intent is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Intent, nameof(Intent)),
        () => Check.Range(Top, 1, 50, nameof(Top)));
}
