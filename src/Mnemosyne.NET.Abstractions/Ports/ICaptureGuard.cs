using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>
/// The capture guard port. An implementation inspects a capture before it is stored, so a redaction or policy check can
/// stop a capture that holds something that must not be kept. Cancellation throws <see cref="OperationCanceledException"/>;
/// it is never returned as a result.
/// </summary>
/// <remarks>
/// The facade calls the guard, when one is registered, for each capture that passes validation, before anything else
/// happens to it. A blocked capture is refused with <see cref="MnemosyneErrorCode.Blocked"/> and nothing is stored. The
/// guard must be registered as a singleton.
/// </remarks>
public interface ICaptureGuard
{
    /// <summary>Inspects one capture.</summary>
    /// <remarks>
    /// The guard receives a valid capture, including <see cref="ReasonletCapture.Answer"/>, which is never stored. Error
    /// codes: <see cref="MnemosyneErrorCode.Unavailable"/> when the guard's backend fails. Any other failure, and a verdict
    /// that fails <see cref="CaptureVerdict.Validate"/>, is an adapter bug: the facade throws
    /// <see cref="InvalidOperationException"/> for it. A guard must not quote the capture in an error, in
    /// <see cref="CaptureVerdict.Guard"/> or in <see cref="CaptureVerdict.Detector"/>.
    /// </remarks>
    /// <param name="capture">The capture to inspect. It is valid.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The verdict.</returns>
    ValueTask<Result<CaptureVerdict, MnemosyneError>> InspectAsync(ReasonletCapture capture, CancellationToken ct);
}
