using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A Reasonlet returned by a similarity search, before ranking.</summary>
/// <param name="Reasonlet">The matched definition. Must not be null.</param>
/// <param name="Similarity">The similarity in the unit interval.</param>
/// <param name="Metrics">The execution metrics of the Reasonlet.</param>
public sealed record ReasonletCandidate(ReasonletDefinition Reasonlet, double Similarity, ReasonletMetrics Metrics)
{
    /// <summary>Validates the candidate.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Reasonlet, nameof(Reasonlet)),
        () => Check.Nested(Reasonlet.Validate(), nameof(Reasonlet)),
        () => Check.UnitInterval(Similarity, nameof(Similarity)),
        () => Check.Nested(Metrics.Validate(), nameof(Metrics)));
}
