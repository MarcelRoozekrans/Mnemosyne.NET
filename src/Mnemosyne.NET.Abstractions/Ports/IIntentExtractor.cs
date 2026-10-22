using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The intent port. Implementations turn a free-form question into a <see cref="ReasonletIntent"/>.</summary>
public interface IIntentExtractor
{
    /// <summary>Extracts the intent of a question.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the question is empty,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the extraction backend fails.
    /// </remarks>
    /// <param name="question">The question text.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The extracted intent.</returns>
    ValueTask<Result<ReasonletIntent, MnemosyneError>> ExtractAsync(string question, CancellationToken ct);
}
