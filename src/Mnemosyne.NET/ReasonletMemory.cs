using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The default <see cref="IReasonletMemory"/>. It resolves intents and names, embeds questions with the host's
/// embedding generator, and delegates storage to an <see cref="IReasonletStore"/> and ranking to an
/// <see cref="IReasonletSelector"/>.
/// </summary>
/// <remarks>
/// An exception from the embedding generator or the intent extractor answers <see cref="MnemosyneErrorCode.Unavailable"/>,
/// unless it is an <see cref="OperationCanceledException"/> while the caller's token is cancelled, which propagates. A
/// cancellation exception while the token is not cancelled is a timeout and answers Unavailable too. The error's detail
/// names the exception type but never holds its message, which can contain the question. An Unavailable result from the
/// extractor is rewrapped with a fixed message for the same reason, and any other error code from it throws
/// <see cref="InvalidOperationException"/>, as an invalid intent does.
/// </remarks>
public sealed class ReasonletMemory : IReasonletMemory
{
    /// <summary>The intent remember stores when neither the capture nor an extractor supplies one.</summary>
    internal const string UnspecifiedIntent = "unspecified";

    /// <summary>The most code points of a name that remember derives from the question.</summary>
    internal const int MaxDefaultNameLength = 200;

    private const string ExtractorFailed = "The intent extractor failed.";

    /// <summary>The most candidates recall asks the store for, the upper bound of <see cref="MnemosyneOptions.CandidatePool"/>.</summary>
    private const int MaxSearchLimit = 200;

    private static readonly char[] LineBreaks = ['\r', '\n'];

    private readonly IReasonletStore _store;
    private readonly IReasonletSelector _selector;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IIntentExtractor? _extractor;
    private readonly MnemosyneOptions _options;

    /// <summary>Initializes the facade.</summary>
    /// <param name="store">The store that holds the Reasonlets.</param>
    /// <param name="selector">The selector that ranks recall candidates.</param>
    /// <param name="embeddings">The generator that embeds questions. It must be the one that made the stored embeddings.</param>
    /// <param name="extractor">The optional intent extractor. Without one, recall searches every intent and remember stores <c>unspecified</c>.</param>
    /// <param name="options">The facade settings. Must be valid.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ReasonletMemory(
        IReasonletStore store,
        IReasonletSelector selector,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IIntentExtractor? extractor,
        MnemosyneOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(options);
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _store = store;
        _selector = selector;
        _embeddings = embeddings;
        _extractor = extractor;
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The intent is the capture's, else the extractor's answer at any confidence, else <c>unspecified</c>. The name is the
    /// capture's, else the first non-blank line of the question, trimmed and cut to 200 code points. The stored capture
    /// carries the same resolved intent and name.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The intent extractor answered with an invalid intent or an error code other than Unavailable.</exception>
    public async ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletCapture capture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var valid = capture.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var intent = await ResolveRememberIntentAsync(capture, ct).ConfigureAwait(false);
        if (intent.IsFailure)
        {
            return intent.Error;
        }

        var name = capture.Name ?? DefaultName(capture.Question);
        var embedding = await EmbedAsync(capture.Question, ct).ConfigureAwait(false);
        if (embedding.IsFailure)
        {
            return embedding.Error;
        }

        var entry = new ReasonletStoreEntry(capture with { Intent = intent.Value, Name = name }, intent.Value, name, embedding.Value);
        return await _store.RememberAsync(entry, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The intent filter is the query's, else the extractor's answer when its confidence is at least
    /// <see cref="MnemosyneOptions.MinIntentConfidence"/>, else none. The store is asked for
    /// <see cref="MnemosyneOptions.CandidatePool"/> candidates, raised to <see cref="ReasonletQuery.Top"/> when that is
    /// larger, and the selector ranks them. A store or selector error is returned unchanged.
    /// <para>
    /// Each recall emits a <c>mnemosyne.recall</c> activity, with the store's own activities nested in it, and counts
    /// and times itself under operation <c>recall</c>, on the <see cref="MnemosyneTelemetry"/> names. No signal carries
    /// the question, intent, scope or any error or exception message.
    /// </para>
    /// <para>
    /// Every exception, a null <paramref name="query"/> included, is reported through the returned task rather than
    /// thrown synchronously, so that the recall's telemetry records it; it surfaces when the task is awaited.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null. Reported through the returned task.</exception>
    /// <exception cref="InvalidOperationException">The intent extractor answered with an invalid intent or an error code other than Unavailable, or the store returned an invalid candidate.</exception>
    public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RecallAsync(ReasonletQuery query, CancellationToken ct) =>
        RecallTelemetry.RunAsync(activity => RecallOperationAsync(query, activity, ct));

    /// <summary>The recall itself, run inside its activity, which it tags as it goes.</summary>
    private async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RecallOperationAsync(
        ReasonletQuery query, Activity? activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecallTelemetry.Query(activity, query);
        var valid = query.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var intent = await ResolveRecallIntentAsync(query, ct).ConfigureAwait(false);
        if (intent.IsFailure)
        {
            return intent.Error;
        }

        RecallTelemetry.IntentFiltered(activity, intent.Value is not null);
        var embedding = await EmbedAsync(query.Question, ct).ConfigureAwait(false);
        if (embedding.IsFailure)
        {
            return embedding.Error;
        }

        var limit = Math.Min(MaxSearchLimit, Math.Max(_options.CandidatePool, query.Top));
        var candidates = await _store.SearchAsync(query.Scope, embedding.Value, intent.Value, limit, ct).ConfigureAwait(false);
        if (candidates.IsFailure)
        {
            return candidates.Error;
        }

        RecallTelemetry.Candidates(activity, candidates.Value.Count);
        EnsureValid(candidates.Value);
        return await _selector.RankAsync(query, candidates.Value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is null.</exception>
    public ValueTask<Result<ExecutionId, MnemosyneError>> RecordExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var valid = Check.All(
            () => Check.Nested(scope.Validate(), nameof(scope)),
            () => Check.NotDefault(id, nameof(id)),
            () => Check.Nested(outcome.Validate(), nameof(outcome)),
            () => Check.Nested(provenance.Validate(), nameof(provenance)));
        return valid.IsFailure
            ? ValueTask.FromResult(Result<ExecutionId, MnemosyneError>.Failure(valid.Error))
            : _store.AppendExecutionAsync(scope, id, outcome, provenance, ct);
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<MnemosyneError>> RecordFeedbackAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        var valid = Check.All(
            () => Check.Nested(scope.Validate(), nameof(scope)),
            () => Check.NotDefault(execution, nameof(execution)),
            () => Check.Nested(feedback.Validate(), nameof(feedback)));
        return valid.IsFailure ? ValueTask.FromResult(valid) : _store.AppendFeedbackAsync(scope, execution, feedback, ct);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="erasure"/> is null.</exception>
    public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(erasure);
        var valid = erasure.Validate();
        return valid.IsFailure
            ? ValueTask.FromResult(Result<ReasonletErasureResult, MnemosyneError>.Failure(valid.Error))
            : _store.EraseAsync(erasure, ct);
    }

    /// <summary>
    /// Derives a name from a question: its first line that is not blank, trimmed and cut to
    /// <see cref="MaxDefaultNameLength"/> code points, then trimmed again. A cut never splits a surrogate pair.
    /// </summary>
    /// <param name="question">The question. Must not be blank.</param>
    /// <returns>The name, which is never blank.</returns>
    internal static string DefaultName(string question)
    {
        // The question is not blank, so at least one line survives the trim.
        var line = question.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var span = line.AsSpan();
        var end = 0;
        for (var count = 0; count < MaxDefaultNameLength && end < span.Length; count++)
        {
            // A lone surrogate decodes as one invalid unit, so it counts as one code point.
            Rune.DecodeFromUtf16(span[end..], out _, out var consumed);
            end += consumed;
        }

        // A cut can land just after a space; the trimmed line itself never ends in one.
        return line[..end].TrimEnd();
    }

    private async ValueTask<Result<string?, MnemosyneError>> ResolveRecallIntentAsync(ReasonletQuery query, CancellationToken ct)
    {
        if (query.Intent is not null || _extractor is null)
        {
            return Result<string?, MnemosyneError>.Success(query.Intent);
        }

        var extracted = await ExtractAsync(_extractor, query.Question, ct).ConfigureAwait(false);
        if (extracted.IsFailure)
        {
            return extracted.Error;
        }

        var confident = extracted.Value.Confidence >= _options.MinIntentConfidence;
        return Result<string?, MnemosyneError>.Success(confident ? extracted.Value.Intent : null);
    }

    private async ValueTask<Result<string, MnemosyneError>> ResolveRememberIntentAsync(ReasonletCapture capture, CancellationToken ct)
    {
        if (capture.Intent is { } intent)
        {
            return Result<string, MnemosyneError>.Success(intent);
        }

        if (_extractor is null)
        {
            return Result<string, MnemosyneError>.Success(UnspecifiedIntent);
        }

        // A stored intent is better than none, so remember keeps the answer whatever its confidence.
        var extracted = await ExtractAsync(_extractor, capture.Question, ct).ConfigureAwait(false);
        return extracted.IsFailure
            ? Result<string, MnemosyneError>.Failure(extracted.Error)
            : Result<string, MnemosyneError>.Success(extracted.Value.Intent);
    }

    private static async ValueTask<Result<ReasonletIntent, MnemosyneError>> ExtractAsync(
        IIntentExtractor extractor, string question, CancellationToken ct)
    {
        Result<ReasonletIntent, MnemosyneError> extracted;
        try
        {
            extracted = await extractor.ExtractAsync(question, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return DependencyFailed(ExtractorFailed, ex);
        }

        if (extracted.IsFailure)
        {
            // The extractor's own text can quote the question, so only its code is kept. The question is valid, so any
            // code but Unavailable is an adapter bug, and passing it on would pass for an invalid query or capture.
            if (extracted.Error.Code != MnemosyneErrorCode.Unavailable)
            {
                throw new InvalidOperationException(
                    $"The intent extractor failed with {extracted.Error.Code}; an extractor given a valid question may only fail with {nameof(MnemosyneErrorCode.Unavailable)}.");
            }

            return new MnemosyneError(MnemosyneErrorCode.Unavailable, ExtractorFailed);
        }

        if (extracted.Value.Validate() is { IsFailure: true } invalid)
        {
            throw new InvalidOperationException($"The intent extractor returned an invalid intent: {invalid.Error.Message}");
        }

        return extracted;
    }

    private async ValueTask<Result<ReadOnlyMemory<float>, MnemosyneError>> EmbedAsync(string text, CancellationToken ct)
    {
        GeneratedEmbeddings<Embedding<float>>? embeddings;
        try
        {
            embeddings = await _embeddings.GenerateAsync([text], options: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return DependencyFailed("The embedding generator failed.", ex);
        }

        if (embeddings is not { Count: > 0 })
        {
            return new MnemosyneError(MnemosyneErrorCode.Unavailable, "The embedding generator returned no embedding.");
        }

        return Result<ReadOnlyMemory<float>, MnemosyneError>.Success(embeddings[0].Vector);
    }

    /// <summary>Throws when the store returned a candidate that breaks an invariant, which is a store bug.</summary>
    private static void EnsureValid(IReadOnlyList<ReasonletCandidate> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var path = $"candidates[{i}]";
            var candidate = candidates[i];
            var valid = candidate is null ? Check.Fail(path, "must not be null") : Check.Nested(candidate.Validate(), path);
            if (valid.IsFailure)
            {
                throw new InvalidOperationException($"The store returned an invalid candidate: {valid.Error.Message}");
            }
        }
    }

    /// <summary>
    /// Returns whether an exception is the caller's cancellation, which propagates. An
    /// <see cref="OperationCanceledException"/> while <paramref name="ct"/> is not cancelled is a dependency timeout,
    /// such as <see cref="HttpClient"/>'s <see cref="TaskCanceledException"/>, and maps to Unavailable instead.
    /// </summary>
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>Maps a dependency's exception to Unavailable, naming its type but dropping its message, which can hold the question.</summary>
    private static MnemosyneError DependencyFailed(string message, Exception ex) =>
        new(MnemosyneErrorCode.Unavailable, message, ex.GetType().FullName);
}
