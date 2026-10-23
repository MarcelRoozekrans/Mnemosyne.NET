using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>What an <see cref="IReasonletEvaluator"/> judges: one run, and the procedure it followed.</summary>
/// <remarks>
/// It carries the procedure's graph, name and intent rather than a <see cref="ReasonletDefinition"/>, because a remember
/// judges its run before the store decides what the capture becomes, so no id, version or status exists yet.
/// Init-only, so later phases can add members without breaking callers.
/// </remarks>
public sealed record ReasonletEvaluationRequest
{
    /// <summary>Gets the procedure that was followed. Must not be null and must be valid.</summary>
    public required ReasonletGraph Graph { get; init; }

    /// <summary>Gets the procedure's name. Must not be blank.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the procedure's intent. Must not be blank.</summary>
    public required string Intent { get; init; }

    /// <summary>Gets the run's question. Must not be blank.</summary>
    public required string Question { get; init; }

    /// <summary>Gets the run's answer. Must not be blank and must not be longer than 32,000 code points. It is never stored.</summary>
    public required string Answer { get; init; }

    /// <summary>Gets the agent's own view of the run.</summary>
    public required ReasonletOutcome Outcome { get; init; }

    /// <summary>Validates the request.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(Graph, nameof(Graph)),
        () => Check.Nested(Graph.Validate(), nameof(Graph)),
        () => Check.NotBlank(Name, nameof(Name)),
        () => Check.NotBlank(Intent, nameof(Intent)),
        () => Check.NotBlank(Question, nameof(Question)),
        () => Check.NotBlank(Answer, nameof(Answer)),
        () => Check.ValidUtf16(Answer, nameof(Answer)),
        () => Check.MaxLength(Answer, EvaluationLimits.MaxRunTextLength, nameof(Answer)),
        () => Check.Nested(Outcome.Validate(), nameof(Outcome)));
}
