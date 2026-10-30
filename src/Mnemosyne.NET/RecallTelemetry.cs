using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The recall facade's OpenTelemetry signals: one <c>mnemosyne.recall</c> activity per recall, and the counters and
/// histograms that count, time and size it, under the names on <see cref="MnemosyneTelemetry"/>. Its source, meter and
/// operation instruments are shared with <see cref="EvaluationTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// The store emits the same <c>mnemosyne.operations</c> and <c>mnemosyne.operation.duration</c> instruments from its
/// own assembly. Both meters share one name, so the two aggregate as one instrument, which is why the names, units and
/// descriptions here match the store's exactly.
/// </para>
/// <para>
/// No signal carries user data: no question, intent, scope, subject or tag text, and no error or exception message.
/// Ids, versions and scores may appear. With no listener attached an activity is never created, and every tag is set
/// through a null-conditional call, so nothing is formatted or boxed for it; the instruments take their tags as a
/// <see cref="TagList"/>, which lives on the stack. The cost is not zero, though: the caller still allocates the body's
/// closure on every recall, as the store's <c>RunAsync</c> does, and the operation count and duration are recorded
/// whether or not a meter listener is attached.
/// </para>
/// </remarks>
internal static class RecallTelemetry
{
    private const string ActivityName = "mnemosyne.recall";
    private const string Operation = "recall";
    private const string OperationTag = "mnemosyne.operation";
    /// <summary>The tag that carries an operation's outcome.</summary>
    internal const string OutcomeTag = "mnemosyne.outcome";
    private const string ErrorTypeTag = "error.type";
    private const string ExceptionEvent = "exception";
    private const string ExceptionTypeTag = "exception.type";
    /// <summary>The outcome of an operation that succeeded.</summary>
    internal const string Ok = "ok";

    /// <summary>The outcome of an operation that threw.</summary>
    internal const string ExceptionOutcome = "exception";

    private const string TopTag = "mnemosyne.recall.top";
    private const string IntentFilteredTag = "mnemosyne.recall.intent_filtered";
    private const string IntentFallbackTag = "mnemosyne.recall.intent_fallback";
    private const string CandidatesTag = "mnemosyne.recall.candidates";
    private const string ReturnedTag = "mnemosyne.recall.returned";
    private const string ChosenIdTag = "mnemosyne.recall.chosen.id";
    private const string ChosenVersionTag = "mnemosyne.recall.chosen.version";
    private const string ChosenScoreTag = "mnemosyne.recall.chosen.score";

    /// <summary>The source of the recall activity.</summary>
    internal static readonly ActivitySource Source = new(MnemosyneTelemetry.ActivitySourceName);

    /// <summary>The meter of the recall instruments.</summary>
    internal static readonly Meter Meter = new(MnemosyneTelemetry.MeterName);

    private static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("mnemosyne.operations", "{operation}", "Mnemosyne operations, by operation and outcome.");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("mnemosyne.operation.duration", "s", "How long Mnemosyne operations take, by operation.");

    private static readonly Histogram<int> Returned =
        Meter.CreateHistogram<int>("mnemosyne.recall.returned", "{match}", "Matches a successful recall returns.");

    private static readonly Counter<long> Empty =
        Meter.CreateCounter<long>("mnemosyne.recall.empty", "{recall}", "Successful recalls that returned nothing.");

    /// <summary>Runs one recall inside its <c>mnemosyne.recall</c> activity, and counts, times and sizes it.</summary>
    /// <remarks>
    /// A success tags what it returned and, when it returned a match, the first one, and records the count. A failure
    /// result sets <c>error.type</c> to the error code's name and the status to Error, and is counted under that name. An
    /// exception, cancellation included, is recorded on the activity as an <c>exception</c> event that carries
    /// <c>exception.type</c> only, never the message or the stack trace, which could quote the question. It sets
    /// <c>error.type</c> to its full type name and the status to Error, is counted as <c>exception</c>, and propagates unchanged.
    /// </remarks>
    /// <param name="body">The recall. It receives the activity, null when no one listens, to set its own tags on.</param>
    internal static async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RunAsync(
        Func<Activity?, ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>> body)
    {
        using var activity = Source.StartActivity(ActivityName);
        var started = Stopwatch.GetTimestamp();
        var outcome = ExceptionOutcome;
        try
        {
            var result = await body(activity).ConfigureAwait(false);
            if (result.IsFailure)
            {
                outcome = ErrorName(result.Error.Code);
                Failed(activity, outcome);
            }
            else
            {
                outcome = Ok;
                Recalled(activity, result.Value);
            }

            return result;
        }
        catch (Exception ex)
        {
            Threw(activity, ex);
            throw;
        }
        finally
        {
            CountOperation(Operation, outcome, started);
        }
    }

    /// <summary>Marks an activity as failed with <paramref name="errorType"/>: it sets <c>error.type</c> and the status to Error.</summary>
    internal static void Failed(Activity? activity, string errorType)
    {
        activity?.SetTag(ErrorTypeTag, errorType);
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    /// <summary>
    /// Records an exception on an activity as an <c>exception</c> event that carries <c>exception.type</c> only, and marks
    /// the activity as failed with the exception's full type name.
    /// </summary>
    internal static void Threw(Activity? activity, Exception ex)
    {
        // The type only: the message and the stack trace could quote user data, which no signal may carry.
        var type = ex.GetType().FullName!;
        activity?.AddEvent(new ActivityEvent(ExceptionEvent, tags: new ActivityTagsCollection { { ExceptionTypeTag, type } }));
        Failed(activity, type);
    }

    /// <summary>
    /// Counts one facade operation under its outcome on <c>mnemosyne.operations</c>, and records its duration since
    /// <paramref name="started"/> on <c>mnemosyne.operation.duration</c>.
    /// </summary>
    internal static void CountOperation(string operation, string outcome, long started)
    {
        Operations.Add(1, new TagList { { OperationTag, operation }, { OutcomeTag, outcome } });
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new TagList { { OperationTag, operation } });
    }

    /// <summary>
    /// Tags a recall with its query, before it is validated, so a failed recall has it too. The intent filter is the
    /// query's until <see cref="IntentFiltered"/> replaces it with the resolved one.
    /// </summary>
    internal static void Query(Activity? activity, ReasonletQuery query)
    {
        activity?.SetTag(TopTag, query.Top);
        activity?.SetTag(IntentFilteredTag, query.Intent is not null);
    }

    /// <summary>Tags a recall with whether its resolved intent filter is set.</summary>
    internal static void IntentFiltered(Activity? activity, bool filtered) => activity?.SetTag(IntentFilteredTag, filtered);

    /// <summary>
    /// Tags a recall that searched every intent because the intent extractor was down. The tag is a bare <c>true</c>: it
    /// carries no error or exception message.
    /// </summary>
    internal static void IntentFallback(Activity? activity) => activity?.SetTag(IntentFallbackTag, true);

    /// <summary>Tags a recall with how many candidates the store returned.</summary>
    internal static void Candidates(Activity? activity, int count) => activity?.SetTag(CandidatesTag, count);

    /// <summary>The name an error code is tagged with, without formatting a defined one.</summary>
    internal static string ErrorName(MnemosyneErrorCode code) => code switch
    {
        MnemosyneErrorCode.Validation => nameof(MnemosyneErrorCode.Validation),
        MnemosyneErrorCode.NotFound => nameof(MnemosyneErrorCode.NotFound),
        MnemosyneErrorCode.Conflict => nameof(MnemosyneErrorCode.Conflict),
        MnemosyneErrorCode.Unavailable => nameof(MnemosyneErrorCode.Unavailable),
        MnemosyneErrorCode.Blocked => nameof(MnemosyneErrorCode.Blocked),
        _ => code.ToString(),
    };

    /// <summary>Tags a successful recall with what it returned and its first match, and records the count.</summary>
    private static void Recalled(Activity? activity, IReadOnlyList<ReasonletMatch> matches)
    {
        activity?.SetTag(ReturnedTag, matches.Count);
        if (matches.Count > 0)
        {
            var chosen = matches[0];
            activity?.SetTag(ChosenIdTag, chosen.Reasonlet.Id.Value.ToString());
            activity?.SetTag(ChosenVersionTag, chosen.Reasonlet.Version);
            activity?.SetTag(ChosenScoreTag, chosen.Score.Total);
        }
        else
        {
            Empty.Add(1);
        }

        Returned.Record(matches.Count);
    }
}
