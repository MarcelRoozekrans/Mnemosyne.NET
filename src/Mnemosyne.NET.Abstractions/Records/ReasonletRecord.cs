using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>One Reasonlet in a subject export: its definition and the metrics of its recorded runs.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletRecord
{
    /// <summary>Gets the exported Reasonlet's definition. Must not be null and must be valid.</summary>
    public required ReasonletDefinition Definition { get; init; }

    /// <summary>Gets the execution metrics of the exported Reasonlet.</summary>
    public required ReasonletMetrics Metrics { get; init; }

    /// <summary>Validates the record.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Definition, nameof(Definition)),
        () => Check.Nested(Definition.Validate(), nameof(Definition)),
        () => Check.Nested(Metrics.Validate(), nameof(Metrics)));
}
