namespace Mnemosyne.NET.Abstractions;

/// <summary>Reports what a prune deleted.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletPruneResult
{
    /// <summary>Gets the number of Reasonlets deleted because they were no longer used.</summary>
    public required int Reasonlets { get; init; }

    /// <summary>
    /// Gets the number of executions deleted: the runs older than the policy's window, plus the runs of the deleted
    /// Reasonlets. Each is counted once.
    /// </summary>
    public required int Executions { get; init; }
}
