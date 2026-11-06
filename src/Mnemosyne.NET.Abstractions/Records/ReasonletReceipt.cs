namespace Mnemosyne.NET.Abstractions;

/// <summary>The receipt returned after a capture. Produced by the library, never by callers.</summary>
/// <remarks>
/// Every capture records a run, an <see cref="ReasonletCaptureKind.ExecutionOnly"/> one included, so every receipt says
/// which verdict counted for that run when it was stored. <see cref="Source"/> is never
/// <see cref="ReasonletVerdictSource.Feedback"/> here, because feedback comes later.
/// Init-only, so later phases can add members without breaking callers.
/// </remarks>
public sealed record ReasonletReceipt
{
    /// <summary>Gets the identifier of the Reasonlet the capture was stored under.</summary>
    public required ReasonletId Id { get; init; }

    /// <summary>Gets the version label the capture was stored under.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the identifier of the recorded execution.</summary>
    public required ExecutionId Execution { get; init; }

    /// <summary>Gets what the capture produced.</summary>
    public required ReasonletCaptureKind Kind { get; init; }

    /// <summary>Gets the lifecycle status of the Reasonlet after the capture.</summary>
    public required ReasonletStatus Status { get; init; }

    /// <summary>
    /// Gets which verdict counted for the recorded run: <see cref="ReasonletVerdictSource.Evaluator"/> when an evaluator judged it,
    /// else <see cref="ReasonletVerdictSource.Agent"/>.
    /// </summary>
    public required ReasonletVerdictSource Source { get; init; }

    /// <summary>Gets the recorded run's effective success when it was stored, the verdict of <see cref="Source"/>.</summary>
    public required bool Success { get; init; }
}
