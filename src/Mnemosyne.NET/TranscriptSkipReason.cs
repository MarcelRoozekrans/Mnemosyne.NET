namespace Mnemosyne.NET;

/// <summary>Why a chat transcript gave nothing to capture.</summary>
/// <remarks>
/// A skip is not an error: the transcript is valid, it just holds no procedure. The values are numbered from 1 and 0 is
/// deliberately undefined, so a <c>default</c> result reads as never having been set.
/// </remarks>
public enum TranscriptSkipReason
{
    /// <summary>No user message has non-blank text, so there is no question.</summary>
    NoQuestion = 1,

    /// <summary>
    /// The last question's run has no successful tool call: none was made, none got a result, or every result carried an
    /// exception. An <c>IChatClient</c> middleware that sits inside function invocation sees this on every call.
    /// </summary>
    NoToolCalls = 2,
}
