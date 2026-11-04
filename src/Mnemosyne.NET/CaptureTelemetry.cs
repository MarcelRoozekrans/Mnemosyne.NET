using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>
/// The OpenTelemetry signals of the background capture: a <c>mnemosyne.capture.run</c> activity, the
/// <c>mnemosyne.capture.runs</c> counter and the <c>mnemosyne.capture.duration</c> histogram for each processed item, and
/// the <c>mnemosyne.capture.queue</c> counter for each item offered to the queue. It shares the recall's source and meter.
/// </summary>
/// <remarks>
/// <para>
/// Start one with <see cref="Start"/> before an item is processed, report how it ended with exactly one of
/// <see cref="Remembered"/>, <see cref="Skipped"/>, <see cref="Failed"/>, <see cref="Threw"/> or <see cref="Cancelled"/>, and dispose it, which
/// counts, times and stops it. On <c>mnemosyne.capture.runs</c> the outcome is <c>new_reasonlet</c>, <c>new_version</c> or
/// <c>execution_only</c> for a stored capture, <c>skipped</c> with <c>mnemosyne.skip_reason</c> <c>no_question</c> or
/// <c>no_tool_calls</c>, the error code's name for an error result, and <c>exception</c> with <c>error.type</c> set to the
/// full type name for an exception.
/// </para>
/// <para>
/// No signal carries the scope, the subject, the question, the answer, a tool or argument name, the run id, or any error
/// or exception message.
/// </para>
/// </remarks>
internal sealed class CaptureTelemetry : IDisposable
{
    /// <summary>The queue outcome of an item accepted by the queue.</summary>
    internal const string EnqueuedOutcome = "enqueued";

    /// <summary>The queue outcome of an item refused because the queue was full.</summary>
    internal const string DroppedFullOutcome = "dropped_full";

    /// <summary>The queue outcome of an item left in the queue at shutdown.</summary>
    internal const string DroppedShutdownOutcome = "dropped_shutdown";

    private const string ActivityName = "mnemosyne.capture.run";
    private const string SkipReasonTag = "mnemosyne.skip_reason";
    private const string ErrorTypeTag = "error.type";
    private const string SkippedOutcome = "skipped";

    private static readonly Counter<long> Runs =
        RecallTelemetry.Meter.CreateCounter<long>("mnemosyne.capture.runs", "{run}", "Background captures, by outcome.");

    private static readonly Histogram<double> Duration =
        RecallTelemetry.Meter.CreateHistogram<double>("mnemosyne.capture.duration", "s", "How long processing one background capture takes.");

    private static readonly Counter<long> Queued =
        RecallTelemetry.Meter.CreateCounter<long>("mnemosyne.capture.queue", "{item}", "Items offered to the capture queue, by outcome.");

    private readonly Activity? _activity;
    private readonly long _started;
    private string _outcome = RecallTelemetry.ExceptionOutcome;
    private string? _skipReason;
    private string? _errorType;
    private bool _cancelled;

    private CaptureTelemetry(Activity? activity, long started)
    {
        _activity = activity;
        _started = started;
    }

    /// <summary>Starts the <c>mnemosyne.capture.run</c> activity, null when no one listens, and the clock.</summary>
    internal static CaptureTelemetry Start() =>
        new(RecallTelemetry.Source.StartActivity(ActivityName), Stopwatch.GetTimestamp());

    /// <summary>Counts one item offered to the queue under <paramref name="outcome"/>.</summary>
    internal static void Queue(string outcome) => Queued.Add(1, new TagList { { RecallTelemetry.OutcomeTag, outcome } });

    /// <summary>Reports a stored capture: the outcome is the kind in snake case.</summary>
    internal void Remembered(ReasonletCaptureKind kind)
    {
        _outcome = kind switch
        {
            ReasonletCaptureKind.NewReasonlet => "new_reasonlet",
            ReasonletCaptureKind.NewVersion => "new_version",
            ReasonletCaptureKind.ExecutionOnly => "execution_only",
            _ => kind.ToString(),
        };
        _activity?.SetTag(RecallTelemetry.OutcomeTag, _outcome);
    }

    /// <summary>Reports a transcript with nothing to capture: outcome <c>skipped</c>, tagged with the reason.</summary>
    internal void Skipped(TranscriptSkipReason reason)
    {
        _outcome = SkippedOutcome;
        _skipReason = reason == TranscriptSkipReason.NoQuestion ? "no_question" : "no_tool_calls";
        _activity?.SetTag(RecallTelemetry.OutcomeTag, _outcome);
        _activity?.SetTag(SkipReasonTag, _skipReason);
    }

    /// <summary>Reports an error result: the outcome and <c>error.type</c> are the code's name.</summary>
    internal void Failed(MnemosyneErrorCode code)
    {
        _outcome = RecallTelemetry.ErrorName(code);
        _activity?.SetTag(RecallTelemetry.OutcomeTag, _outcome);
        RecallTelemetry.Failed(_activity, _outcome);
    }

    /// <summary>Reports an exception: outcome <c>exception</c>, and <c>error.type</c> and an event that carry only its full type name.</summary>
    internal void Threw(Exception ex)
    {
        _outcome = RecallTelemetry.ExceptionOutcome;
        _errorType = ex.GetType().FullName!;
        _activity?.SetTag(RecallTelemetry.OutcomeTag, _outcome);
        RecallTelemetry.Threw(_activity, ex);
    }

    /// <summary>
    /// Reports an item cut short by its caller's cancellation, as at shutdown: no run outcome is counted and the activity
    /// is not marked as failed, since the queue counts the item as dropped. The duration is still recorded.
    /// </summary>
    internal void Cancelled() => _cancelled = true;

    /// <summary>Counts and times the item, and stops the activity.</summary>
    public void Dispose()
    {
        if (_cancelled)
        {
            Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds);
            _activity?.Dispose();
            return;
        }

        var tags = new TagList { { RecallTelemetry.OutcomeTag, _outcome } };
        if (_skipReason is not null)
        {
            tags.Add(SkipReasonTag, _skipReason);
        }

        if (_errorType is not null)
        {
            tags.Add(ErrorTypeTag, _errorType);
        }

        Runs.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds);
        _activity?.Dispose();
    }
}
