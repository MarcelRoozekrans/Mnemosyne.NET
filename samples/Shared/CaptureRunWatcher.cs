using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.Samples;

/// <summary>
/// Tells a sample when a background capture has finished, and how. A capture that is blocked, skipped or only records an
/// execution changes nothing in the store, so a store poll cannot wait for it; the <c>mnemosyne.capture.runs</c> counter
/// is added to when any capture finishes, whatever its outcome.
/// </summary>
/// <remarks>
/// Create it before the capture starts, since it only hears runs that finish afterwards. The outcome is the value of the
/// counter's <c>mnemosyne.outcome</c> tag: <c>new_reasonlet</c>, <c>new_version</c>, <c>execution_only</c>,
/// <c>skipped</c>, the name of an error code such as <c>Blocked</c>, or <c>exception</c>. The meter is shared by the
/// process, so the watcher also hears captures of other providers in it.
/// </remarks>
public sealed class CaptureRunWatcher : IDisposable
{
    private const string InstrumentName = "mnemosyne.capture.runs";
    private const string OutcomeTag = "mnemosyne.outcome";

    private readonly Channel<string> _outcomes = Channel.CreateUnbounded<string>();
    private readonly MeterListener _listener = new();

    /// <summary>Starts listening for finished captures.</summary>
    public CaptureRunWatcher()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MnemosyneTelemetry.MeterName && instrument.Name == InstrumentName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnRun);
        _listener.Start();
    }

    /// <summary>Waits for the next finished capture and returns its outcome, in the order the captures finished.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <exception cref="TimeoutException">No capture finished within <paramref name="timeout"/>.</exception>
    public async Task<string> NextOutcomeAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await _outcomes.Reader.ReadAsync(ct).AsTask().WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No capture finished within {timeout}.");
        }
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => _listener.Dispose();

    private void OnRun(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == OutcomeTag && tag.Value is string outcome)
            {
                _outcomes.Writer.TryWrite(outcome);
                return;
            }
        }
    }
}
