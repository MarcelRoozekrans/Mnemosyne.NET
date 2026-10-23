namespace Mnemosyne.NET.Abstractions;

/// <summary>The receipt returned after a capture. Produced by the library, never by callers.</summary>
/// <remarks>
/// Every capture records a run, an <see cref="ReasonletCaptureKind.ExecutionOnly"/> one included, so every receipt says
/// which verdict counted for that run when it was stored. <paramref name="Source"/> is never
/// <see cref="ReasonletVerdictSource.Feedback"/> here, because feedback comes later.
/// </remarks>
/// <param name="Id">The identifier of the Reasonlet the capture was stored under.</param>
/// <param name="Version">The version label the capture was stored under.</param>
/// <param name="Execution">The identifier of the recorded execution.</param>
/// <param name="Kind">What the capture produced.</param>
/// <param name="Status">The lifecycle status of the Reasonlet after the capture.</param>
/// <param name="Source">
/// Which verdict counted for the recorded run: <see cref="ReasonletVerdictSource.Evaluator"/> when an evaluator judged it,
/// else <see cref="ReasonletVerdictSource.Agent"/>.
/// </param>
/// <param name="Success">The recorded run's effective success when it was stored, the verdict of <paramref name="Source"/>.</param>
public sealed record ReasonletReceipt(
    ReasonletId Id,
    string Version,
    ExecutionId Execution,
    ReasonletCaptureKind Kind,
    ReasonletStatus Status,
    ReasonletVerdictSource Source,
    bool Success);
