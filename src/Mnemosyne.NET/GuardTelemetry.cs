using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>
/// The OpenTelemetry signals of one capture guard call: a <c>mnemosyne.guard</c> activity, the
/// <c>mnemosyne.guard.inspections</c> counter and the <c>mnemosyne.guard.duration</c> histogram, and the shared operation
/// count and duration under operation <c>guard</c>. It shares the recall's source and meter, under the names on
/// <see cref="MnemosyneTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Start one with <see cref="Start"/> before the guard is called, report how the call ended with exactly one of
/// <see cref="Inspected"/>, <see cref="Failed"/> or <see cref="Threw"/>, and dispose it, which counts, times and stops it.
/// On <c>mnemosyne.guard.inspections</c> the outcome is <c>allowed</c> or <c>blocked</c> for a valid verdict, the error
/// code's name for an error result, and <c>exception</c>, with <c>error.type</c> set to the full type name, for an
/// exception. The shared operation count records <c>ok</c> for any valid verdict, since the guard answered, and the same
/// error and exception outcomes otherwise. A timeout is reported as Unavailable, since the facade treats it as one.
/// </para>
/// <para>
/// No signal carries the question, the answer, the intent, the name, the graph, or any error or exception message. The
/// guard's name and the detector's, which the guard contract limits to naming them, are tagged only from a verdict that
/// passed validation. A failed guard call, whether it was unavailable, timed out, threw, answered another error code or
/// returned an invalid verdict, has no name to tag, so its signals carry no <c>mnemosyne.guard</c> tag.
/// </para>
/// </remarks>
internal sealed class GuardTelemetry : IDisposable
{
    private const string ActivityName = "mnemosyne.guard";
    private const string Operation = "guard";
    private const string GuardTag = "mnemosyne.guard";
    private const string BlockedTag = "mnemosyne.guard.blocked";
    private const string DetectorTag = "mnemosyne.guard.detector";
    private const string BlockedEvent = "mnemosyne.capture.blocked";

    /// <summary>The inspection outcome of a capture the guard let through.</summary>
    internal const string AllowedOutcome = "allowed";

    /// <summary>The inspection outcome of a capture the guard blocked.</summary>
    internal const string BlockedOutcome = "blocked";

    private static readonly Counter<long> Inspections =
        RecallTelemetry.Meter.CreateCounter<long>("mnemosyne.guard.inspections", "{inspection}", "Capture guard calls, by guard and outcome.");

    private static readonly Histogram<double> Duration =
        RecallTelemetry.Meter.CreateHistogram<double>("mnemosyne.guard.duration", "s", "How long capture guard calls take, by guard.");

    private readonly Activity? _activity;
    private readonly long _started;
    private string _outcome = RecallTelemetry.ExceptionOutcome;
    private string _operationOutcome = RecallTelemetry.ExceptionOutcome;
    private string? _guard;

    private GuardTelemetry(Activity? activity, long started)
    {
        _activity = activity;
        _started = started;
    }

    /// <summary>Starts the <c>mnemosyne.guard</c> activity, null when no one listens, and the clock.</summary>
    internal static GuardTelemetry Start() =>
        new(RecallTelemetry.Source.StartActivity(ActivityName), Stopwatch.GetTimestamp());

    /// <summary>
    /// Reports a valid verdict: outcome <c>allowed</c> or <c>blocked</c>, operation outcome <c>ok</c>, tagged with the
    /// guard's name, whether it blocked, and the detector when it did. A block also adds a <c>mnemosyne.capture.blocked</c>
    /// event carrying the guard and the detector.
    /// </summary>
    internal void Inspected(CaptureVerdict verdict)
    {
        _outcome = verdict.IsBlocked ? BlockedOutcome : AllowedOutcome;
        _operationOutcome = RecallTelemetry.Ok;
        _guard = verdict.Guard;
        if (_activity is not { } activity)
        {
            return;
        }

        activity.SetTag(GuardTag, verdict.Guard);
        activity.SetTag(BlockedTag, verdict.IsBlocked);
        if (verdict.IsBlocked)
        {
            activity.SetTag(DetectorTag, verdict.Detector);
            activity.AddEvent(new ActivityEvent(
                BlockedEvent,
                tags: new ActivityTagsCollection { { GuardTag, verdict.Guard }, { DetectorTag, verdict.Detector } }));
        }
    }

    /// <summary>Reports an error result, or a timeout as Unavailable: the outcome and <c>error.type</c> are the code's name.</summary>
    internal void Failed(MnemosyneErrorCode code)
    {
        _outcome = RecallTelemetry.ErrorName(code);
        _operationOutcome = _outcome;
        RecallTelemetry.Failed(_activity, _outcome);
    }

    /// <summary>
    /// Reports an exception: outcome <c>exception</c>, and an <c>exception</c> event and <c>error.type</c> that carry only
    /// its full type name.
    /// </summary>
    internal void Threw(Exception ex)
    {
        _outcome = RecallTelemetry.ExceptionOutcome;
        _operationOutcome = RecallTelemetry.ExceptionOutcome;
        RecallTelemetry.Threw(_activity, ex);
    }

    /// <summary>Counts and times the guard call, under the guard's name when it answered, and stops the activity.</summary>
    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        if (_guard is { } guard)
        {
            Inspections.Add(1, new TagList { { GuardTag, guard }, { RecallTelemetry.OutcomeTag, _outcome } });
            Duration.Record(elapsed, new TagList { { GuardTag, guard } });
        }
        else
        {
            Inspections.Add(1, new TagList { { RecallTelemetry.OutcomeTag, _outcome } });
            Duration.Record(elapsed);
        }

        RecallTelemetry.CountOperation(Operation, _operationOutcome, _started);
        _activity?.Dispose();
    }
}
