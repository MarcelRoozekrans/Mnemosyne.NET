namespace Mnemosyne.NET;

/// <summary>The counts that every integration shares for runs it could not hand to capture.</summary>
public static class ReasonletCaptures
{
    /// <summary>
    /// Counts one finished run that could not be handed to capture, such as a sink that failed to resolve or a run that
    /// failed to build or offer: <c>mnemosyne.capture.queue</c> with outcome <c>exception</c> and <c>error.type</c> set to
    /// the exception's full type name. Nothing of the exception but its type is recorded.
    /// </summary>
    /// <param name="exception">The exception that stopped the hand-off.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static void CountFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        CaptureTelemetry.QueueFailed(exception);
    }
}
