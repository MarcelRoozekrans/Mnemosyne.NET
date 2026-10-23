using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A retention policy: what a prune deletes because it is old or no longer used.</summary>
/// <remarks>
/// Init-only, so later phases can add members without breaking callers. At least one window must be set, or the policy
/// would delete nothing. Each window is measured back from one instant, the store's clock when the prune starts, so a
/// long prune has a fixed target.
/// </remarks>
public sealed record ReasonletRetention
{
    /// <summary>Gets the scope to prune, or null to prune every scope.</summary>
    public ReasonletScope? Scope { get; init; }

    /// <summary>
    /// Gets how old a run record must be to be deleted, or null to keep every run. Must be positive when set. The
    /// Reasonlets' counters keep pruned runs, because those runs still happened.
    /// </summary>
    public TimeSpan? ExecutionsOlderThan { get; init; }

    /// <summary>
    /// Gets how long a Reasonlet must have gone without being executed or remembered, together with every version
    /// below it, to be deleted, or null to keep every Reasonlet. Must be positive when set.
    /// </summary>
    public TimeSpan? ReasonletsUnusedFor { get; init; }

    /// <summary>Validates the policy.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Scope is { } scope ? Check.Nested(scope.Validate(), nameof(Scope)) : UnitResult<MnemosyneError>.Success(),
        () => ExecutionsOlderThan is null && ReasonletsUnusedFor is null
            ? Check.Fail(nameof(ExecutionsOlderThan), $"must be set when {nameof(ReasonletsUnusedFor)} is not, or the policy prunes nothing")
            : UnitResult<MnemosyneError>.Success(),
        () => Check.PositiveWhenPresent(ExecutionsOlderThan, nameof(ExecutionsOlderThan)),
        () => Check.PositiveWhenPresent(ReasonletsUnusedFor, nameof(ReasonletsUnusedFor)));
}
