using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A Reasonlet returned by a similarity search, before ranking.</summary>
/// <param name="Reasonlet">The matched definition. Must not be null.</param>
/// <param name="Similarity">The similarity in [0, 1], computed as <c>max(0, 1 - cosine distance)</c>, which matches pgvector's <c>&lt;=&gt;</c> cosine distance operator.</param>
/// <param name="Metrics">The execution metrics of the Reasonlet.</param>
/// <param name="ParentMetrics">The execution metrics of the Reasonlet's parent, or null for a root or a Reasonlet whose lineage link was cut.</param>
public sealed record ReasonletCandidate(
    ReasonletDefinition Reasonlet, double Similarity, ReasonletMetrics Metrics, ReasonletMetrics? ParentMetrics = null)
{
    /// <summary>Validates the candidate.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Reasonlet, nameof(Reasonlet)),
        () => Check.Nested(Reasonlet.Validate(), nameof(Reasonlet)),
        () => Check.UnitInterval(Similarity, nameof(Similarity)),
        () => Check.Nested(Metrics.Validate(), nameof(Metrics)),
        () => ParentMetrics is { } parent ? Check.Nested(parent.Validate(), nameof(ParentMetrics)) : UnitResult<MnemosyneError>.Success());
}
