namespace Mnemosyne.NET.Abstractions;

/// <summary>The receipt returned after a run is recorded. Produced by the library, never by callers.</summary>
/// <remarks>
/// It says which verdict counted when the run was stored. Later feedback with a <see cref="ReasonletFeedback.Worked"/>
/// verdict can override it, so <paramref name="Source"/> is never <see cref="ReasonletVerdictSource.Feedback"/> here.
/// </remarks>
/// <param name="Execution">The identifier of the recorded execution.</param>
/// <param name="Source">
/// Which verdict counted when the run was stored: <see cref="ReasonletVerdictSource.Evaluator"/> when an evaluator judged
/// it, else <see cref="ReasonletVerdictSource.Agent"/>.
/// </param>
/// <param name="Success">The run's effective success when it was stored, the verdict of <paramref name="Source"/>.</param>
public sealed record ReasonletRunReceipt(ExecutionId Execution, ReasonletVerdictSource Source, bool Success);
