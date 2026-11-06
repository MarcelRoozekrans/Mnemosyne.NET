namespace Mnemosyne.NET.Abstractions;

/// <summary>The receipt returned after a run is recorded. Produced by the library, never by callers.</summary>
/// <remarks>
/// It says which verdict counted when the run was stored. Later feedback with a <see cref="ReasonletFeedback.Worked"/>
/// verdict can override it, so <see cref="Source"/> is never <see cref="ReasonletVerdictSource.Feedback"/> here.
/// Init-only, so later phases can add members without breaking callers.
/// </remarks>
public sealed record ReasonletRunReceipt
{
    /// <summary>Gets the identifier of the recorded execution.</summary>
    public required ExecutionId Execution { get; init; }

    /// <summary>
    /// Gets which verdict counted when the run was stored: <see cref="ReasonletVerdictSource.Evaluator"/> when an evaluator judged
    /// it, else <see cref="ReasonletVerdictSource.Agent"/>.
    /// </summary>
    public required ReasonletVerdictSource Source { get; init; }

    /// <summary>Gets the run's effective success when it was stored, the verdict of <see cref="Source"/>.</summary>
    public required bool Success { get; init; }
}
