namespace Mnemosyne.NET.Abstractions;

/// <summary>The receipt returned after a capture. Produced by the library, never by callers.</summary>
/// <param name="Id">The identifier of the Reasonlet the capture was stored under.</param>
/// <param name="Version">The version label the capture was stored under.</param>
/// <param name="Execution">The identifier of the recorded execution.</param>
/// <param name="Kind">What the capture produced.</param>
/// <param name="Status">The lifecycle status of the Reasonlet after the capture.</param>
public sealed record ReasonletReceipt(
    ReasonletId Id,
    string Version,
    ExecutionId Execution,
    ReasonletCaptureKind Kind,
    ReasonletStatus Status);
