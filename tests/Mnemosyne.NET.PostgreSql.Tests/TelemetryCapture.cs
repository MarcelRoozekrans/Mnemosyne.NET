using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// Captures what Mnemosyne emits inside one test: its activities and its measurements. Shared with the unit tests,
/// which link this file.
/// </summary>
/// <remarks>
/// Listeners are global, so a test in another class that runs at the same time is heard too. The capture starts a
/// parent activity, which becomes <see cref="Activity.Current"/> for the rest of the test and flows into every call it
/// awaits, and it keeps only what belongs to that trace: an activity by its <see cref="Activity.TraceId"/>, and a
/// measurement by the trace of <see cref="Activity.Current"/> when it is recorded. A measurement callback runs
/// synchronously on the recording call, inside that call's execution context, so it sees the trace of the test that
/// made the call, never another's.
/// </remarks>
internal sealed class TelemetryCapture : IDisposable
{
    private readonly Activity _parent;
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<CapturedMeasurement> _measurements = new();

    public TelemetryCapture()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MnemosyneTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // The parent is started before the listener is added, so it is set whenever this runs.
                if (activity.TraceId == _parent!.TraceId)
                {
                    _activities.Enqueue(activity);
                }
            },
        };
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MnemosyneTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));

        // The parent first, so that nothing is heard before there is a trace to match.
        _parent = new Activity("telemetry-test").Start();
        ActivitySource.AddActivityListener(_activityListener);
        _meterListener.Start();
    }

    /// <summary>The Mnemosyne activities of this test's trace that have stopped, in the order they stopped.</summary>
    public IReadOnlyList<Activity> Activities => [.. _activities];

    /// <summary>The Mnemosyne measurements recorded in this test's trace, in the order they were recorded.</summary>
    public IReadOnlyList<CapturedMeasurement> Measurements => [.. _measurements];

    /// <summary>The measurements of one instrument.</summary>
    public IReadOnlyList<CapturedMeasurement> Of(string instrument) => [.. _measurements.Where(m => m.Instrument == instrument)];

    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
        _parent.Stop();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (Activity.Current?.TraceId == _parent.TraceId)
        {
            _measurements.Enqueue(new CapturedMeasurement(instrument.Name, value, tags.ToArray()));
        }
    }
}

/// <summary>One measurement, with its tags.</summary>
internal sealed record CapturedMeasurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
{
    /// <summary>The value of a tag, or null when the measurement has no such tag.</summary>
    public object? Tag(string key) => Tags.FirstOrDefault(tag => tag.Key == key).Value;
}
