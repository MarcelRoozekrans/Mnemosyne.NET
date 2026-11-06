using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A Reasonlet awaiting human review, as listed by the review gate. Produced by the library, never by callers.</summary>
/// <remarks>
/// Carries what a reviewer needs to decide: the definition, graph included, and the metrics of the runs recorded so far.
/// A Reasonlet is pending when the review gate is on and it has been neither approved nor rejected.
/// Init-only, so later phases can add members without breaking callers.
/// </remarks>
public sealed record ReasonletReview
{
    /// <summary>Gets the pending Reasonlet's definition. Must not be null and must be valid.</summary>
    public required ReasonletDefinition Definition { get; init; }

    /// <summary>Gets the execution metrics of the pending Reasonlet.</summary>
    public required ReasonletMetrics Metrics { get; init; }

    /// <summary>Validates the review.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Definition, nameof(Definition)),
        () => Check.Nested(Definition.Validate(), nameof(Definition)),
        () => Check.Nested(Metrics.Validate(), nameof(Metrics)));
}
