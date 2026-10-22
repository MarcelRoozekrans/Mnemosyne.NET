namespace Mnemosyne.NET.Abstractions;

/// <summary>Reports what an erasure removed.</summary>
/// <remarks>Init-only, so later phases can add members, such as feedback counts, without breaking callers.</remarks>
public sealed record ReasonletErasureResult
{
    /// <summary>Gets the number of Reasonlets removed.</summary>
    public required int Reasonlets { get; init; }

    /// <summary>
    /// Gets the number of executions removed: those of the removed Reasonlets, plus, for an erasure by subject,
    /// the subject's executions recorded on Reasonlets that were kept.
    /// </summary>
    public required int Executions { get; init; }
}
