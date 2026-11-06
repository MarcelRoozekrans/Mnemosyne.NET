using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A Reasonlet returned by a similarity search, before ranking.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletCandidate
{
    /// <summary>Gets the matched definition. Must not be null.</summary>
    public required ReasonletDefinition Reasonlet { get; init; }

    /// <summary>Gets the similarity in [0, 1], computed as <c>max(0, 1 - cosine distance)</c>, which matches pgvector's <c>&lt;=&gt;</c> cosine distance operator.</summary>
    public required double Similarity { get; init; }

    /// <summary>Gets the execution metrics of the Reasonlet.</summary>
    public required ReasonletMetrics Metrics { get; init; }

    /// <summary>Gets the execution metrics of the Reasonlet's parent, or null for a root or a Reasonlet whose lineage link was cut.</summary>
    public ReasonletMetrics? ParentMetrics { get; init; }

    /// <summary>Gets the id of the root of the Reasonlet's version tree, reached by <c>ParentId</c> within the scope. A root's is its own id, and so is that of a version whose parent was erased or lies outside the scope.</summary>
    public required ReasonletId LineageId { get; init; }

    /// <summary>Validates the candidate.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Reasonlet, nameof(Reasonlet)),
        () => Check.NotDefault(LineageId, nameof(LineageId)),
        () => Check.Nested(Reasonlet.Validate(), nameof(Reasonlet)),
        () => Check.UnitInterval(Similarity, nameof(Similarity)),
        () => Check.Nested(Metrics.Validate(), nameof(Metrics)),
        () => ParentMetrics is { } parent ? Check.Nested(parent.Validate(), nameof(ParentMetrics)) : UnitResult<MnemosyneError>.Success());
}
