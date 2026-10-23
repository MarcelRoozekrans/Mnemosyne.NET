using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A ranked Reasonlet returned to the caller.</summary>
/// <param name="Reasonlet">The matched definition. Must not be null.</param>
/// <param name="Score">The ranking score.</param>
public sealed record ReasonletMatch(ReasonletDefinition Reasonlet, ReasonletScore Score)
{
    /// <summary>Validates the match.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Reasonlet, nameof(Reasonlet)),
        () => Check.Nested(Reasonlet.Validate(), nameof(Reasonlet)),
        () => Check.Nested(Score.Validate(), nameof(Score)));
}
