using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;
using Npgsql;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql;

/// <summary>
/// The store's OpenTelemetry signals: one activity per operation, and the counters and histograms that count and time
/// them, under the names on <see cref="MnemosyneTelemetry"/>.
/// </summary>
/// <remarks>
/// No signal carries user data: no scope, subject, question, intent, name, graph, tags or run id. Ids and versions are
/// opaque, so they may appear. With no listener attached an activity is never created, and every tag is set through a
/// null-conditional call, so nothing is formatted or boxed for it; the instruments take their tags as a
/// <see cref="TagList"/>, which lives on the stack.
/// </remarks>
internal static class StoreTelemetry
{
    private const string ActivityPrefix = "mnemosyne.";
    private const string OperationTag = "mnemosyne.operation";
    private const string OutcomeTag = "mnemosyne.outcome";
    private const string ErrorTypeTag = "error.type";
    private const string ExceptionEvent = "exception";
    private const string ExceptionTypeTag = "exception.type";
    private const string DbStatusCodeTag = "db.response.status_code";
    private const string VerdictSourceTag = "mnemosyne.verdict_source";
    private const string Ok = "ok";
    private const string ExceptionOutcome = "exception";

    /// <summary>The source of every store activity.</summary>
    internal static readonly ActivitySource Source = new(MnemosyneTelemetry.ActivitySourceName);

    /// <summary>The meter of every store instrument.</summary>
    internal static readonly Meter Meter = new(MnemosyneTelemetry.MeterName);

    private static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("mnemosyne.operations", "{operation}", "Mnemosyne operations, by operation and outcome.");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("mnemosyne.operation.duration", "s", "How long Mnemosyne operations take, by operation.");

    private static readonly Counter<long> Remembered =
        Meter.CreateCounter<long>("mnemosyne.reasonlets.remembered", "{capture}", "Successful remembers, by capture kind.");

    private static readonly Histogram<int> SearchReturned =
        Meter.CreateHistogram<int>("mnemosyne.search.returned", "{candidate}", "Candidates a successful search returns.");

    private static readonly Counter<long> ErasedReasonlets =
        Meter.CreateCounter<long>("mnemosyne.erased.reasonlets", "{reasonlet}", "Reasonlets erased.");

    private static readonly Counter<long> ErasedExecutions =
        Meter.CreateCounter<long>("mnemosyne.erased.executions", "{execution}", "Executions erased.");

    private static readonly Counter<long> PrunedReasonlets =
        Meter.CreateCounter<long>("mnemosyne.pruned.reasonlets", "{reasonlet}", "Reasonlets pruned, by committed batch.");

    private static readonly Counter<long> PrunedExecutions =
        Meter.CreateCounter<long>("mnemosyne.pruned.executions", "{execution}", "Executions pruned, by committed batch.");

    /// <summary>
    /// Runs one store operation inside its activity, <c>mnemosyne.</c> followed by <paramref name="operation"/>, and
    /// counts and times it.
    /// </summary>
    /// <remarks>
    /// A failure result sets <c>error.type</c> to the error code's name and the status to Error, and is counted under
    /// that name. An exception, cancellation included, is recorded on the activity as an <c>exception</c> event that
    /// carries <c>exception.type</c> only, never the message or the stack trace, which could quote user data. It sets
    /// <c>error.type</c> to its full type name and the status to Error, is counted as <c>exception</c>, and propagates unchanged.
    /// A database exception that carries a SQLSTATE also sets <c>db.response.status_code</c> to it, the OpenTelemetry
    /// database convention's name: a SQLSTATE is a five-character code from a fixed list, so it carries no user data,
    /// and it says what failed where the message no longer can.
    /// </remarks>
    /// <param name="operation">The operation's name, such as <c>remember</c>.</param>
    /// <param name="body">The operation. It receives the activity, null when no one listens, to set its own tags on.</param>
    /// <param name="errorOf">Answers the error code of a failed result, or null for a success.</param>
    internal static async ValueTask<TResult> RunAsync<TResult>(
        string operation, Func<Activity?, ValueTask<TResult>> body, Func<TResult, MnemosyneErrorCode?> errorOf)
    {
        // The name is built only when someone listens, so an unobserved store allocates nothing for it.
        using var activity = Source.HasListeners() ? Source.StartActivity(string.Concat(ActivityPrefix, operation)) : null;
        var started = Stopwatch.GetTimestamp();
        var outcome = ExceptionOutcome;
        try
        {
            var result = await body(activity).ConfigureAwait(false);
            if (errorOf(result) is { } code)
            {
                outcome = ErrorName(code);
                activity?.SetTag(ErrorTypeTag, outcome);
                activity?.SetStatus(ActivityStatusCode.Error);
            }
            else
            {
                outcome = Ok;
            }

            return result;
        }
        catch (Exception ex)
        {
            // The type only: the message and the stack trace could quote user data, which no signal may carry.
            var type = ex.GetType().FullName;
            activity?.AddEvent(new ActivityEvent(ExceptionEvent, tags: new ActivityTagsCollection { { ExceptionTypeTag, type } }));
            activity?.SetTag(ErrorTypeTag, type);
            if (ex is NpgsqlException { SqlState: { } sqlState })
            {
                activity?.SetTag(DbStatusCodeTag, sqlState);
            }

            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            Operations.Add(1, new TagList { { OperationTag, operation }, { OutcomeTag, outcome } });
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new TagList { { OperationTag, operation } });
        }
    }

    /// <summary>Runs an operation that answers a <see cref="Result{T, E}"/>, as the three-argument <c>RunAsync</c> does.</summary>
    internal static ValueTask<Result<T, MnemosyneError>> RunAsync<T>(
        string operation, Func<Activity?, ValueTask<Result<T, MnemosyneError>>> body) => RunAsync(operation, body, ErrorOf);

    /// <summary>Runs an operation that answers a <see cref="UnitResult{E}"/>, as the three-argument <c>RunAsync</c> does.</summary>
    internal static ValueTask<UnitResult<MnemosyneError>> RunAsync(
        string operation, Func<Activity?, ValueTask<UnitResult<MnemosyneError>>> body) => RunAsync(operation, body, ErrorOf);

    /// <summary>Answers the error code of a failed result, or null for a success.</summary>
    internal static MnemosyneErrorCode? ErrorOf<T>(Result<T, MnemosyneError> result) => result.IsFailure ? result.Error.Code : null;

    /// <summary>Answers the error code of a failed result, or null for a success.</summary>
    internal static MnemosyneErrorCode? ErrorOf(UnitResult<MnemosyneError> result) => result.IsFailure ? result.Error.Code : null;

    /// <summary>The name an error code is tagged with, without formatting a defined one.</summary>
    internal static string ErrorName(MnemosyneErrorCode code) => code switch
    {
        MnemosyneErrorCode.Validation => nameof(MnemosyneErrorCode.Validation),
        MnemosyneErrorCode.NotFound => nameof(MnemosyneErrorCode.NotFound),
        MnemosyneErrorCode.Conflict => nameof(MnemosyneErrorCode.Conflict),
        MnemosyneErrorCode.Unavailable => nameof(MnemosyneErrorCode.Unavailable),
        _ => code.ToString(),
    };

    /// <summary>The name a capture kind is tagged with, without formatting a defined one.</summary>
    internal static string CaptureKindName(ReasonletCaptureKind kind) => kind switch
    {
        ReasonletCaptureKind.NewReasonlet => nameof(ReasonletCaptureKind.NewReasonlet),
        ReasonletCaptureKind.NewVersion => nameof(ReasonletCaptureKind.NewVersion),
        ReasonletCaptureKind.ExecutionOnly => nameof(ReasonletCaptureKind.ExecutionOnly),
        _ => kind.ToString(),
    };

    /// <summary>Tags a successful remember with what it stored, and counts it by capture kind.</summary>
    internal static void Remember(Activity? activity, ReasonletReceipt receipt)
    {
        var kind = CaptureKindName(receipt.Kind);
        activity?.SetTag("mnemosyne.capture_kind", kind);
        activity?.SetTag("mnemosyne.reasonlet.id", receipt.Id.Value.ToString());
        activity?.SetTag("mnemosyne.reasonlet.version", receipt.Version);
        Remembered.Add(1, new TagList { { "mnemosyne.capture_kind", kind } });
    }

    /// <summary>
    /// Tags an execution append with the source of the run's verdict, before it runs, so a failed append has it too:
    /// <c>evaluator</c> when the run comes with an evaluation, else <c>agent</c>.
    /// </summary>
    internal static void ExecutionVerdictSource(Activity? activity, bool evaluated) =>
        activity?.SetTag(VerdictSourceTag, evaluated ? "evaluator" : "agent");

    /// <summary>Tags feedback that changed the run's effective success with verdict source <c>feedback</c>.</summary>
    internal static void FeedbackVerdictSource(Activity? activity) => activity?.SetTag(VerdictSourceTag, "feedback");

    /// <summary>Tags a search with its limit, before it runs, so a failed search has it too.</summary>
    internal static void SearchLimit(Activity? activity, int limit) => activity?.SetTag("mnemosyne.search.limit", limit);

    /// <summary>Tags a successful search with what it returned, and records the count.</summary>
    internal static void Searched(Activity? activity, IReadOnlyList<ReasonletCandidate> candidates)
    {
        activity?.SetTag("mnemosyne.search.returned", candidates.Count);
        if (candidates.Count > 0)
        {
            activity?.SetTag("mnemosyne.search.top_similarity", candidates[0].Similarity);
        }

        SearchReturned.Record(candidates.Count);
    }

    /// <summary>Tags an erase with what it erases by, before it runs, so a failed erase has it too.</summary>
    internal static void EraseBy(Activity? activity, ReasonletErasure erasure) =>
        activity?.SetTag("mnemosyne.erase.by", erasure.SubjectId is null ? "scope" : "subject");

    /// <summary>Tags a successful erase with what it removed, and counts it.</summary>
    internal static void Erased(Activity? activity, ReasonletErasureResult erased)
    {
        activity?.SetTag("mnemosyne.erased.reasonlets", erased.Reasonlets);
        activity?.SetTag("mnemosyne.erased.executions", erased.Executions);
        ErasedReasonlets.Add(erased.Reasonlets);
        ErasedExecutions.Add(erased.Executions);
    }

    /// <summary>
    /// Tags a prune with what its committed batches deleted, and counts it. Called however the prune ends, because
    /// a committed batch stays deleted when a later one fails.
    /// </summary>
    internal static void Pruned(Activity? activity, PruneProgress progress)
    {
        activity?.SetTag("mnemosyne.pruned.reasonlets", progress.Reasonlets);
        activity?.SetTag("mnemosyne.pruned.executions", progress.Executions);
        PrunedReasonlets.Add(progress.Reasonlets);
        PrunedExecutions.Add(progress.Executions);
    }
}
