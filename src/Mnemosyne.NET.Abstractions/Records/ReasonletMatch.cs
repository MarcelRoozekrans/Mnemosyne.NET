using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A ranked Reasonlet returned to the caller.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletMatch
{
    /// <summary>Gets the matched definition. Must not be null.</summary>
    public required ReasonletDefinition Reasonlet { get; init; }

    /// <summary>Gets the ranking score.</summary>
    public required ReasonletScore Score { get; init; }

    /// <summary>Gets the evidence behind the score's success rate.</summary>
    public required ReasonletEvidence Evidence { get; init; }

    /// <summary>Gets the id of the root of the Reasonlet's version tree, reached by <c>ParentId</c> within the scope. A root's is its own id, and so is that of a version whose parent was erased.</summary>
    public required ReasonletId LineageId { get; init; }

    /// <summary>Validates the match.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Reasonlet, nameof(Reasonlet)),
        () => Check.NotDefault(LineageId, nameof(LineageId)),
        () => Check.Nested(Reasonlet.Validate(), nameof(Reasonlet)),
        () => Check.Nested(Score.Validate(), nameof(Score)),
        () => Check.Nested(Evidence.Validate(), nameof(Evidence)));
}
