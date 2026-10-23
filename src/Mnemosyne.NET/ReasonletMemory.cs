using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The default <see cref="IReasonletMemory"/>. It resolves intents and names, embeds questions with the host's
/// embedding generator, and delegates storage to an <see cref="IReasonletStore"/> and ranking to an
/// <see cref="IReasonletSelector"/>.
/// </summary>
/// <remarks>
/// <para>
/// An exception from the embedding generator or the intent extractor counts as that dependency being down, unless it is
/// an <see cref="OperationCanceledException"/> while the caller's token is cancelled, which propagates. A cancellation
/// exception while the token is not cancelled is a timeout and counts as down too. An Unavailable result from the
/// extractor also counts as down, and any other error code from it throws <see cref="InvalidOperationException"/>, as an
/// invalid intent does, because both are adapter bugs.
/// </para>
/// <para>
/// A generator that is down answers <see cref="MnemosyneErrorCode.Unavailable"/>, and so does one that returns an
/// embedding the store cannot use. An extractor that is down answers Unavailable at remember, so a wrong intent is never
/// stored; recall instead searches every intent. No error carries an exception's message or the extractor's own text,
/// which can quote the question: the detail names the exception type, or the reason an embedding is unusable.
/// </para>
/// </remarks>
public sealed class ReasonletMemory : IReasonletMemory
{
    /// <summary>The intent remember stores when neither the capture nor an extractor supplies one.</summary>
    internal const string UnspecifiedIntent = "unspecified";

    /// <summary>The most code points of a name that remember derives from the question.</summary>
    internal const int MaxDefaultNameLength = 200;

    private const string ExtractorFailed = "The intent extractor failed.";

    private const string UnusableEmbedding = "The embedding generator returned an embedding the store cannot use.";

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
    /// carries the same resolved intent and name. An extractor that is down answers <see cref="MnemosyneErrorCode.Unavailable"/>
    /// rather than storing a guessed intent. An embedding the store rejects answers Unavailable, as it does at recall.
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
        var receipt = await _store.RememberAsync(entry, ct).ConfigureAwait(false);
        return receipt.IsFailure ? RejectedEmbedding(receipt.Error) : receipt;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The intent filter is the query's, else the extractor's answer when its confidence is at least
    /// <see cref="MnemosyneOptions.MinIntentConfidence"/>, else none. An extractor that is down does not fail the recall:
    /// it searches every intent instead, and tags <c>mnemosyne.recall.intent_fallback</c>. The store is asked for
    /// <see cref="MnemosyneOptions.CandidatePool"/> candidates, raised to <see cref="ReasonletQuery.Top"/> when that is
    /// larger, and the selector ranks them. A store or selector error is returned unchanged, except that the store's
    /// Validation on the embedding answers <see cref="MnemosyneErrorCode.Unavailable"/>.
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

        var (intent, fellBack) = await ResolveRecallIntentAsync(query, ct).ConfigureAwait(false);
        RecallTelemetry.IntentFiltered(activity, intent is not null);
        if (fellBack)
        {
            RecallTelemetry.IntentFallback(activity);
        }

        var embedding = await EmbedAsync(query.Question, ct).ConfigureAwait(false);
        if (embedding.IsFailure)
        {
            return embedding.Error;
        }

        var limit = Math.Min(MaxSearchLimit, Math.Max(_options.CandidatePool, query.Top));
        var candidates = await _store.SearchAsync(query.Scope, embedding.Value, intent, limit, ct).ConfigureAwait(false);
        if (candidates.IsFailure)
        {
            return RejectedEmbedding(candidates.Error);
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

    /// <summary>
    /// Resolves recall's intent filter. An extractor that is down gives no filter and sets <c>FellBack</c>: a missing
    /// filter only widens the search, while failing would leave the agent with nothing.
    /// </summary>
    private async ValueTask<(string? Intent, bool FellBack)> ResolveRecallIntentAsync(ReasonletQuery query, CancellationToken ct)
    {
        if (query.Intent is not null || _extractor is null)
        {
            return (query.Intent, false);
        }

        // ExtractAsync fails only with Unavailable; every other extractor failure throws.
        var extracted = await ExtractAsync(_extractor, query.Question, ct).ConfigureAwait(false);
        if (extracted.IsFailure)
        {
            return (null, true);
        }

        var confident = extracted.Value.Confidence >= _options.MinIntentConfidence;
        return (confident ? extracted.Value.Intent : null, false);
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

        var vector = embeddings[0].Vector;
        return UnusableReason(vector.Span) is { } reason
            ? new MnemosyneError(MnemosyneErrorCode.Unavailable, UnusableEmbedding, reason)
            : Result<ReadOnlyMemory<float>, MnemosyneError>.Success(vector);
    }

    /// <summary>
    /// Returns why no store can use an embedding, or null when it is usable as far as the facade can tell. Cosine
    /// similarity needs at least one value, finite values and a nonzero norm. The norm is summed in single precision, as
    /// pgvector sums it, so squares that overflow count as an unusable norm too. The facade cannot know the store's
    /// dimension, so a wrong length is left to the store; see <see cref="RejectedEmbedding"/>.
    /// </summary>
    internal static string? UnusableReason(ReadOnlySpan<float> embedding)
    {
        if (embedding.IsEmpty)
        {
            return "length: the embedding is empty";
        }

        var squares = 0f;
        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
            {
                return "non-finite: the embedding holds a NaN or infinite value";
            }

            squares += value * value;
        }

        return squares > 0f && float.IsFinite(squares) ? null : "norm: the embedding has a zero norm, or its squares overflow";
    }

    /// <summary>
    /// Maps the store's <see cref="MnemosyneErrorCode.Validation"/> on the embedding, such as a length that does not match
    /// its dimension, to <see cref="MnemosyneErrorCode.Unavailable"/>: the generator made the embedding, not the caller,
    /// so the caller's input is not what is invalid. Every other error passes through unchanged.
    /// </summary>
    /// <remarks>
    /// The facade validated everything else the store checks, so the store's path is the only sign of an embedding
    /// failure: a Validation message whose path is <c>embedding</c>, in any case, followed by <c>:</c> or an index. The
    /// store's own message, which describes only the embedding, becomes the detail.
    /// </remarks>
    internal static MnemosyneError RejectedEmbedding(MnemosyneError error) =>
        error.Code == MnemosyneErrorCode.Validation && IsEmbeddingPath(error.Message)
            ? new MnemosyneError(MnemosyneErrorCode.Unavailable, UnusableEmbedding, error.Message)
            : error;

    private static bool IsEmbeddingPath(string message)
    {
        const string Path = "embedding";
        return message.Length > Path.Length
            && message.StartsWith(Path, StringComparison.OrdinalIgnoreCase)
            && message[Path.Length] is ':' or '[';
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
