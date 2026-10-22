using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The storage port. Implementations persist Reasonlets, executions and feedback.</summary>
public interface IReasonletStore
{
    /// <summary>Stores an entry, making the three-way ParentId decision atomically.</summary>
    /// <remarks>
    /// The store decides between a new Reasonlet, an execution only and a new version in one atomic step.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or ID,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the parent does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Conflict"/> when the write loses a race the store detects,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="entry">The entry to store.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The receipt describing what was stored.</returns>
    ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct);

    /// <summary>Gets a Reasonlet definition.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or ID,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the ID does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="scope">The scope to look in.</param>
    /// <param name="id">The Reasonlet identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The definition.</returns>
    ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct);

    /// <summary>Appends an execution to a Reasonlet.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or ID,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the Reasonlet does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="scope">The scope the Reasonlet lives in.</param>
    /// <param name="id">The Reasonlet that was executed.</param>
    /// <param name="outcome">The outcome of the execution.</param>
    /// <param name="provenance">Where the execution came from.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The identifier of the appended execution.</returns>
    ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionAsync(ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct);

    /// <summary>Appends feedback to an execution.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or ID,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the execution does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="scope">The scope the execution lives in.</param>
    /// <param name="execution">The execution the feedback is about.</param>
    /// <param name="feedback">The feedback.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Success, or the error.</returns>
    ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct);

    /// <summary>Searches a scope by embedding similarity.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the limit is not positive or an argument breaks another invariant, such as a <c>default</c> scope,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="scope">The scope to search.</param>
    /// <param name="embedding">The embedding of the query's question, made by the same generator as the stored embeddings, so stored and query vectors embed the same kind of text.</param>
    /// <param name="intent">An optional intent text to narrow the search.</param>
    /// <param name="limit">The maximum number of candidates to return.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The candidates, before ranking.</returns>
    ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct);

    /// <summary>Erases a scope's Reasonlets and executions, or only those of one subject.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or ID,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="erasure">What to erase.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The numbers of Reasonlets and executions removed.</returns>
    ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct);
}
