using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The caller-facing facade of Reasonlet procedural memory. Cancellation throws <see cref="OperationCanceledException"/>; it is never returned as a result.</summary>
public interface IReasonletMemory
{
    /// <summary>Stores a captured Reasonlet, or records an execution against an existing one.</summary>
    /// <remarks>
    /// The outcome depends on <see cref="ReasonletCapture.ParentId"/> and the captured graph:
    /// <list type="table">
    /// <listheader><term>Capture</term><description>Result</description></listheader>
    /// <item><term>No ParentId</term><description>A new Reasonlet at version 1.0, kind <see cref="ReasonletCaptureKind.NewReasonlet"/>.</description></item>
    /// <item><term>ParentId set and the graph equal to the parent's</term><description>An execution only, recorded on the parent with the capture's outcome and provenance. Kind <see cref="ReasonletCaptureKind.ExecutionOnly"/>.</description></item>
    /// <item><term>ParentId set and the graph different</term><description>A new version, as a child with lineage. Kind <see cref="ReasonletCaptureKind.NewVersion"/>.</description></item>
    /// </list>
    /// Two concurrent captures with the same parent but different graphs create sibling versions.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the capture breaks an invariant,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the parent does not exist in the capture's scope (including a parent that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Conflict"/> when the write loses a race the store detects, such as the parent being erased between lookup and insert,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store, the embedding generator or the intent extractor fails, or the generator
    /// returns an embedding the store cannot use: empty, not of the store's <see cref="IReasonletStore.Dimensions"/>, non-finite or of zero norm. An extractor that fails is never
    /// replaced by a guessed intent, so a wrong intent is never stored.
    /// </remarks>
    /// <param name="capture">The capture to store.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The receipt describing what the capture produced.</returns>
    ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletCapture capture, CancellationToken ct);

    /// <summary>Finds and ranks the Reasonlets that best answer a query.</summary>
    /// <remarks>
    /// A match must have a similarity of at least <see cref="ReasonletQuery.MinSimilarity"/> when that is set. When absent, the
    /// selector's own cut-off applies; the default selector uses <c>ReasonletRankingOptions.MinSimilarity</c>, 0.5.
    /// The result is empty, and still a success, when nothing passes.
    /// An intent extractor that fails does not fail the recall: it searches every intent instead, since a missing filter only widens the search.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the query breaks an invariant,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store, the embedding generator or the selector fails, or the generator
    /// returns an embedding the store cannot use: empty, not of the store's <see cref="IReasonletStore.Dimensions"/>, non-finite or of zero norm.
    /// </remarks>
    /// <param name="query">The query.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The ranked matches, best first.</returns>
    ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RecallAsync(ReasonletQuery query, CancellationToken ct);

    /// <summary>Records one run of an existing Reasonlet.</summary>
    /// <remarks>
    /// The run's <see cref="ReasonletRun.Answer"/>, when given with its question, is for an evaluator to judge, and is never
    /// stored. Without an evaluator, the agent's own <see cref="ReasonletOutcome.Success"/> counts.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the run breaks an invariant,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the Reasonlet does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="run">The run to record.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The identifier of the recorded execution.</returns>
    ValueTask<Result<ExecutionId, MnemosyneError>> RecordExecutionAsync(ReasonletRun run, CancellationToken ct);

    /// <summary>Attaches feedback to a recorded execution.</summary>
    /// <remarks>
    /// Feedback replaces the execution's earlier scores. When <see cref="ReasonletFeedback.Worked"/> is present it becomes the
    /// run's verdict, overriding the evaluator's and the agent's. Feedback with <c>Worked</c> null keeps an earlier verdict,
    /// so a verdict can be changed, by recording the other value, but not retracted.
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when an argument breaks an invariant,
    /// <see cref="MnemosyneErrorCode.NotFound"/> when the execution does not exist in the scope (including one that exists only in another scope),
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="scope">The scope the execution lives in.</param>
    /// <param name="execution">The execution the feedback is about.</param>
    /// <param name="feedback">The feedback.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Success, or the error.</returns>
    ValueTask<UnitResult<MnemosyneError>> RecordFeedbackAsync(ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct);

    /// <summary>Erases a scope's Reasonlets and executions, or only those of one subject.</summary>
    /// <remarks>
    /// Error codes: <see cref="MnemosyneErrorCode.Validation"/> when the erasure breaks an invariant,
    /// <see cref="MnemosyneErrorCode.Unavailable"/> when the backing store fails.
    /// </remarks>
    /// <param name="erasure">What to erase.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The numbers of Reasonlets and executions removed.</returns>
    ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct);
}
