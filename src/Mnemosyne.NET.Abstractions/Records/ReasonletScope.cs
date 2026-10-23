using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Names the memory partition a Reasonlet belongs to.</summary>
/// <param name="Value">The scope name. Must not be blank.</param>
public readonly record struct ReasonletScope(string Value)
{
    /// <summary>Validates the scope.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.NotBlank(Value, nameof(Value));
}
