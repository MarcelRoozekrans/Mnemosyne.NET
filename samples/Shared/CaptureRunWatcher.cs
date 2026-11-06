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
    /// <exception cref="ObjectDisposedException">The watcher is disposed, or was while waiting.</exception>
    public async Task<string> NextOutcomeAsync(TimeSpan timeout, CancellationToken ct)
    {
        // The timeout cancels the read itself, so an abandoned read cannot swallow the next outcome.
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            return await _outcomes.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No capture finished within {timeout}.");
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(CaptureRunWatcher));
        }
    }

    /// <summary>Stops listening. A wait still pending throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _listener.Dispose();
        _outcomes.Writer.TryComplete();
    }

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
