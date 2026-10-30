using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>
/// The failure reports of one timed dependency call's telemetry, which the facade's shared timeout helper makes when the
/// call throws, times out or is cancelled by the caller. The judge's and the guard's telemetry implement it, and each
/// reports a successful answer through its own method.
/// </summary>
internal interface IDependencyCallTelemetry
{
    /// <summary>Reports an error result, or a timeout as Unavailable.</summary>
    /// <param name="code">The error code.</param>
    void Failed(MnemosyneErrorCode code);

    /// <summary>Reports an exception, by its type alone.</summary>
    /// <param name="ex">The exception.</param>
    void Threw(Exception ex);
}
