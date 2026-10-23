using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The default <see cref="IReasonletMemory"/>. It resolves intents and names, embeds questions with the host's
/// embedding generator, judges runs with an optional <see cref="IReasonletEvaluator"/>, and delegates storage to an
/// <see cref="IReasonletStore"/> and ranking to an <see cref="IReasonletSelector"/>.
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
/// embedding the store cannot use: empty, not of the store's <see cref="IReasonletStore.Dimensions"/>, non-finite or of
/// zero norm. The facade checks this before it calls the store. An extractor that is down answers Unavailable at remember, so a wrong intent is never
/// stored; recall instead searches every intent. No error carries an exception's message or the extractor's own text,
/// which can quote the question: the detail names the exception type, or the reason an embedding is unusable.
/// </para>
/// <para>
/// The evaluator follows the same rules, except that a judge that is down never fails the call: the run is stored
/// unjudged, and the agent's own outcome counts. A judge that throws, times out or answers Unavailable is down. Any other
/// error code from it, or an invalid evaluation, throws <see cref="InvalidOperationException"/>, whose message names only
/// the code or the failing path. The run's answer reaches the evaluator and nothing else: it is never passed to the
/// store, nor placed in an error, an exception message or a signal.
/// </para>
/// <para>
/// Each judge call emits a <c>mnemosyne.evaluate</c> activity, tagged with the judge's name, success and score when it
/// answered, and counts and times itself on <c>mnemosyne.evaluations</c> and <c>mnemosyne.evaluation.duration</c> and
/// under operation <c>evaluate</c>. A judge that is down shows there as a failure, though the call itself succeeds. A
/// failed judge call has no name to tag, so its signals carry no <c>mnemosyne.evaluator</c> tag.
/// </para>
/// </remarks>
public sealed class ReasonletMemory : IReasonletMemory
{
    /// <summary>The intent remember stores when neither the capture nor an extractor supplies one.</summary>
    internal const string UnspecifiedIntent = "unspecified";

    /// <summary>The most code points of a name that remember derives from the question.</summary>
    internal const int MaxDefaultNameLength = 200;

    private const string ExtractorFailed = "The intent extractor failed.";

    private const string EvaluatorFailed = "The evaluator failed.";

    private const string UnusableEmbedding = "The embedding generator returned an embedding the store cannot use.";

    /// <summary>The most candidates recall asks the store for, the upper bound of <see cref="MnemosyneOptions.CandidatePool"/>.</summary>
    private const int MaxSearchLimit = 200;

    private static readonly char[] LineBreaks = ['\r', '\n'];

    private readonly IReasonletStore _store;
    private readonly IReasonletSelector _selector;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IIntentExtractor? _extractor;
    private readonly IReasonletEvaluator? _evaluator;
    private readonly MnemosyneOptions _options;

    /// <summary>Initializes the facade.</summary>
    /// <param name="store">The store that holds the Reasonlets.</param>
    /// <param name="selector">The selector that ranks recall candidates.</param>
    /// <param name="embeddings">The generator that embeds questions. It must be the one that made the stored embeddings.</param>
    /// <param name="extractor">The optional intent extractor. Without one, recall searches every intent and remember stores <c>unspecified</c>.</param>
    /// <param name="evaluator">
    /// The optional evaluator. With one, each run recorded or remembered with an answer is judged; without one, the agent's
    /// own <see cref="ReasonletOutcome.Success"/> counts.
    /// </param>
    /// <param name="options">The facade settings. Must be valid.</param>
    /// <remarks>
    /// The facade keeps every dependency for its whole life, and <c>AddMnemosyne</c> registers it as a singleton, so the
    /// extractor and the evaluator, like the store and the generator, must be singletons too. A scoped registration fails
    /// scope validation when the facade is resolved.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ReasonletMemory(
        IReasonletStore store,
        IReasonletSelector selector,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IIntentExtractor? extractor,
        IReasonletEvaluator? evaluator,
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
        _evaluator = evaluator;
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The intent is the capture's, else the extractor's answer at any confidence, else <c>unspecified</c>. The name is the
    /// capture's, else the first non-blank line of the question, trimmed and cut to 200 code points. The stored capture
    /// carries the same resolved intent and name. An extractor that is down answers <see cref="MnemosyneErrorCode.Unavailable"/>
    /// rather than storing a guessed intent. An embedding the store cannot use answers Unavailable, as it does at recall.
    /// With an evaluator registered and an <see cref="ReasonletCapture.Answer"/> given, the run is judged after the intent
    /// and name are resolved and before the question is embedded, and the entry carries the verdict but never the answer. A
    /// judge that is down leaves the run unjudged rather than failing the remember.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The intent extractor answered with an invalid intent or an error code other than Unavailable, or the evaluator
    /// answered with an invalid evaluation or an error code other than Unavailable.
    /// </exception>
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
        ReasonletEvaluation? evaluation = null;
        if (_evaluator is not null && capture.Answer is { } answer)
        {
            evaluation = await JudgeAsync(
                _evaluator,
                new ReasonletEvaluationRequest
                {
                    Graph = capture.Graph,
                    Name = name,
                    Intent = intent.Value,
                    Question = capture.Question,
                    Answer = answer,
                    Outcome = capture.Outcome,
                },
                ct).ConfigureAwait(false);
        }

        var embedding = await EmbedAsync(capture.Question, ct).ConfigureAwait(false);
        if (embedding.IsFailure)
        {
            return embedding.Error;
        }

        // The answer is for an evaluator alone and is never stored, so it is stripped here, as the entry requires.
        var entry = new ReasonletStoreEntry(
            capture with { Intent = intent.Value, Name = name, Answer = null }, intent.Value, name, embedding.Value, evaluation);
        return await _store.RememberAsync(entry, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The intent filter is the query's, else the extractor's answer when its confidence is at least
    /// <see cref="MnemosyneOptions.MinIntentConfidence"/>, else none. An extractor that is down does not fail the recall:
    /// it searches every intent instead, and tags <c>mnemosyne.recall.intent_fallback</c>. The store is asked for
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
            return candidates.Error;
        }

        RecallTelemetry.Candidates(activity, candidates.Value.Count);
        EnsureValid(candidates.Value);
        return await _selector.RankAsync(query, candidates.Value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// With an evaluator registered and both <see cref="ReasonletRun.Question"/> and <see cref="ReasonletRun.Answer"/>
    /// given, the facade reads the Reasonlet from the store for its graph, name and intent, judges the run, and passes the
    /// verdict to the store. A Reasonlet that does not exist answers <see cref="MnemosyneErrorCode.NotFound"/> before the
    /// judge is called, and any other read error is returned unchanged. Otherwise the run is stored unjudged and the
    /// agent's own outcome counts. A judge that is down stores the run unjudged too, rather than failing the call.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The evaluator answered with an invalid evaluation or an error code other than Unavailable, or the store returned an
    /// invalid Reasonlet.
    /// </exception>
    public ValueTask<Result<ExecutionId, MnemosyneError>> RecordExecutionAsync(ReasonletRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var valid = run.Validate();
        if (valid.IsFailure)
        {
            return ValueTask.FromResult(Result<ExecutionId, MnemosyneError>.Failure(valid.Error));
        }

        // A valid run has both texts or neither, so the answer alone decides whether there is something to judge.
        return _evaluator is not null && run.Answer is not null
            ? RecordJudgedAsync(_evaluator, run, ct)
            : _store.AppendExecutionAsync(run.Scope, run.Id, run.Outcome, run.Provenance, evaluation: null, ct);
    }

    /// <summary>Reads the run's Reasonlet, judges the run against it, and appends the run with the verdict.</summary>
    private async ValueTask<Result<ExecutionId, MnemosyneError>> RecordJudgedAsync(
        IReasonletEvaluator evaluator, ReasonletRun run, CancellationToken ct)
    {
        var definition = await _store.GetAsync(run.Scope, run.Id, ct).ConfigureAwait(false);
        if (definition.IsFailure)
        {
            return definition.Error;
        }

        if (Check.Nested(definition.Value.Validate(), "reasonlet") is { IsFailure: true } invalid)
        {
            throw new InvalidOperationException($"The store returned an invalid Reasonlet: {invalid.Error.Message}");
        }

        var evaluation = await JudgeAsync(
            evaluator,
            new ReasonletEvaluationRequest
            {
                Graph = definition.Value.Graph,
                Name = definition.Value.Name,
                Intent = definition.Value.Intent,
                Question = run.Question!,
                Answer = run.Answer!,
                Outcome = run.Outcome,
            },
            ct).ConfigureAwait(false);
        return await _store.AppendExecutionAsync(run.Scope, run.Id, run.Outcome, run.Provenance, evaluation, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Judges a run and returns the verdict, or null when the judge is down: a broken judge must not lose the run or fail
    /// the agent's call, so the agent's own outcome counts instead.
    /// </summary>
    private static async ValueTask<ReasonletEvaluation?> JudgeAsync(
        IReasonletEvaluator evaluator, ReasonletEvaluationRequest request, CancellationToken ct)
    {
        var evaluated = await EvaluateAsync(evaluator, request, ct).ConfigureAwait(false);
        return evaluated.IsSuccess ? evaluated.Value : null;
    }

    /// <summary>
    /// Calls the evaluator inside its <c>mnemosyne.evaluate</c> activity. It fails only with Unavailable, when the judge
    /// is down, with the detail naming an exception's type. An error code other than Unavailable or an invalid
    /// evaluation is an adapter bug and throws, and the caller's own cancellation propagates. The activity records how the
    /// judge call ended, a judge that is down included.
    /// </summary>
    private static async ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(
        IReasonletEvaluator evaluator, ReasonletEvaluationRequest request, CancellationToken ct)
    {
        using var telemetry = EvaluationTelemetry.Start();
        Result<ReasonletEvaluation, MnemosyneError> evaluated;
        try
        {
            evaluated = await evaluator.EvaluateAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCallerCancellation(ex, ct))
        {
            telemetry.Threw(ex);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Not the caller's cancellation, so a timeout, which counts as the judge being down.
            telemetry.Failed(MnemosyneErrorCode.Unavailable);
            return DependencyFailed(EvaluatorFailed, ex);
        }
        catch (Exception ex)
        {
            telemetry.Threw(ex);
            return DependencyFailed(EvaluatorFailed, ex);
        }

        if (evaluated.IsFailure)
        {
            telemetry.Failed(evaluated.Error.Code);

            // The evaluator's own text can quote the question or the answer, so only its code is kept. The request is
            // valid, so any code but Unavailable is an adapter bug.
            if (evaluated.Error.Code != MnemosyneErrorCode.Unavailable)
            {
                throw new InvalidOperationException(
                    $"The evaluator failed with {evaluated.Error.Code}; an evaluator given a valid request may only fail with {nameof(MnemosyneErrorCode.Unavailable)}.");
            }

            return new MnemosyneError(MnemosyneErrorCode.Unavailable, EvaluatorFailed);
        }

        if (evaluated.Value.Validate() is { IsFailure: true } invalid)
        {
            var bug = new InvalidOperationException($"The evaluator returned an invalid evaluation: {invalid.Error.Message}");
            telemetry.Threw(bug);
            throw bug;
        }

        telemetry.Judged(evaluated.Value);
        return evaluated;
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
        return UnusableReason(vector.Span, _store.Dimensions) is { } reason
            ? new MnemosyneError(MnemosyneErrorCode.Unavailable, UnusableEmbedding, reason)
            : Result<ReadOnlyMemory<float>, MnemosyneError>.Success(vector);
    }

    /// <summary>
    /// Returns why the store cannot use an embedding, or null when it can. The embedding must have the store's
    /// <see cref="IReasonletStore.Dimensions"/> values, and cosine similarity needs finite values and a nonzero norm. The
    /// norm is summed in single precision, as pgvector sums it, so squares that overflow count as an unusable norm too.
    /// The reason holds only numbers, never text.
    /// </summary>
    internal static string? UnusableReason(ReadOnlySpan<float> embedding, int dimensions)
    {
        if (embedding.IsEmpty)
        {
            return "length: the embedding is empty";
        }

        if (embedding.Length != dimensions)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"length: the embedding has {embedding.Length} values, but the store's dimension is {dimensions}");
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
