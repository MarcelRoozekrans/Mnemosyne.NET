namespace Mnemosyne.NET;

/// <summary>
/// Where an integration hands its finished runs: the bounded background capture queue that
/// <c>AddReasonletCapture</c> registers.
/// </summary>
public interface IReasonletCaptureSink
{
    /// <summary>
    /// Offers one finished run without waiting. Whether the run ended on a call nobody answered is decided now, because a
    /// function invocation layer may mark the calls it invokes as informational only after this returns, while the run still
    /// waits in the queue.
    /// </summary>
    /// <remarks>
    /// The check for a pending call reads the run's messages, so malformed input, such as a null message or a message with
    /// null contents, makes this call throw. A caller that must never fail wraps the call and counts the failure with
    /// <see cref="ReasonletCaptures.CountFailed"/>, as the chat middleware does.
    /// </remarks>
    /// <param name="run">The finished run.</param>
    /// <returns>
    /// True when the run was accepted; false when the queue is full or shut down, which this call has then counted as a
    /// drop itself. It does not throw for a full or closed queue.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    bool TryCapture(ReasonletCaptureRun run);
}
