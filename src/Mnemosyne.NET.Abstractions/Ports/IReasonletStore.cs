using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The storage port. Implementations persist Reasonlets, executions and feedback. Cancellation throws <see cref="OperationCanceledException"/>; it is never returned as a result.</summary>
public interface IReasonletStore
{
    /// <summary>
    /// Gets the length every stored and queried embedding must have. It is fixed when the store is configured, and a
    /// store answers <see cref="MnemosyneErrorCode.Validation"/> for an embedding of any other length. The facade checks
    /// a generated embedding against it before calling the store.
    /// </summary>
    int Dimensions { get; }

    /// <summary>Stores an entry, making the three-way ParentId decision atomically.</summary>
    /// <remarks>
    /// The store decides between a new Reasonlet, an execution only and a new version in one atomic step.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the entry fails <see cref="ReasonletStoreEntry.Validate"/>,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the parent does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Conflict"/> when the write loses a race the store detects,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="entry">The entry to store.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// The receipt describing what was stored, with the verdict source and effective success of the run it recorded, derived
    /// as <see cref="AppendExecutionAsync"/> derives them, from <see cref="ReasonletStoreEntry.Evaluation"/>.
    /// </returns>
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

    /// <summary>Appends an execution to a Reasonlet, with the evaluator's verdict on it when there is one.</summary>
    /// <remarks>
    /// The store derives the run's verdict itself, so every store applies one rule: the effective success is a human's
    /// <see cref="ReasonletFeedback.Worked"/> verdict, else the evaluator's <see cref="ReasonletEvaluation.Success"/>, else
    /// the agent's <see cref="ReasonletOutcome.Success"/>, and the success counters count the effective success.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or ID,
    /// or an evaluation that fails <see cref="ReasonletEvaluation.Validate"/>,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the Reasonlet does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="scope">The scope the Reasonlet lives in.</param>
    /// <param name="id">The Reasonlet that was executed.</param>
    /// <param name="outcome">The outcome of the execution.</param>
    /// <param name="provenance">Where the execution came from.</param>
    /// <param name="evaluation">The evaluator's verdict on the run, or null when no evaluator judged it.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// The receipt of the appended run: its execution identifier, the verdict source the store derived,
    /// <see cref="ReasonletVerdictSource.Evaluator"/> when <paramref name="evaluation"/> is given and
    /// <see cref="ReasonletVerdictSource.Agent"/> otherwise, and that verdict's success.
    /// </returns>
    ValueTask<Result<ReasonletRunReceipt, MnemosyneError>> AppendExecutionAsync(ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct);

    /// <summary>Appends feedback to an execution.</summary>
    /// <remarks>
    /// Feedback replaces the execution's earlier scores. When <see cref="ReasonletFeedback.Worked"/> is present it becomes the
    /// run's verdict, overriding the evaluator's and the agent's, and the success counters follow the effective success.
    /// Feedback with <c>Worked</c> null keeps an earlier verdict, so a verdict can be changed, by recording the other value,
    /// but not retracted.
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
    /// Returns only <see cref="ReasonletStatus.Active"/> Reasonlets, never <see cref="ReasonletStatus.PendingReview"/> ones.
    /// Each candidate's <see cref="ReasonletCandidate.Similarity"/> must be <c>max(0, 1 - cosine distance)</c>,
    /// which is in [0, 1] and matches pgvector's <c>&lt;=&gt;</c> cosine distance operator.
    /// Each candidate must also carry <see cref="ReasonletMetrics.LastExecutedAt"/> in its <see cref="ReasonletCandidate.Metrics"/>,
    /// and <see cref="ReasonletCandidate.ParentMetrics"/> when the Reasonlet has a parent in its scope, null otherwise.
    /// The default selector ranks on both: a store that leaves <c>ParentMetrics</c> null disables inheritance, and one that
    /// leaves <c>LastExecutedAt</c> at its default makes every candidate's freshness zero.
    /// Both metrics must also carry <see cref="ReasonletMetrics.JudgedRuns"/> and <see cref="ReasonletMetrics.JudgedSuccesses"/>,
    /// the runs an evaluator or a human judged and how many of them succeeded. Zero is the valid value for "nothing judged",
    /// so a store that leaves them at zero makes the default selector rank every run as one only the agent judged, at
    /// <c>ReasonletRankingOptions.AgentVerdictWeight</c>.
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
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant, such as a <c>default</c> scope or a blank subject,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="erasure">What to erase.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The numbers of Reasonlets and executions removed.</returns>
    ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct);

    /// <summary>Deletes the run records and Reasonlets that a retention policy no longer keeps.</summary>
    /// <remarks>
    /// The host runs it, for example on a schedule; recall never prunes. A prune may commit its work in several
    /// batches, and a failure keeps the batches committed before it, so it is safe to run again.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the policy breaks an invariant, such as no window, a window that is not positive or a <c>default</c> scope,
    /// <see cref="MnemosyneErrorCode.Conflict"/> when a concurrent change keeps conflicting with the prune,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="retention">What to keep.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The numbers of Reasonlets and executions deleted.</returns>
    ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneAsync(ReasonletRetention retention, CancellationToken ct);
}
