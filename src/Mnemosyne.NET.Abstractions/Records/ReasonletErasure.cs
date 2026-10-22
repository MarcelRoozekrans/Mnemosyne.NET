using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A request to erase stored Reasonlets.</summary>
/// <param name="Scope">The scope to erase from.</param>
/// <param name="SubjectId">An optional subject to limit the erasure to. When present it must not be blank.</param>
public sealed record ReasonletErasure(ReasonletScope Scope, string? SubjectId)
{
    /// <summary>Validates the erasure request.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => SubjectId is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(SubjectId, nameof(SubjectId)));
}
