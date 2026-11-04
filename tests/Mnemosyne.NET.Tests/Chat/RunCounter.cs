namespace Mnemosyne.NET.Tests.Chat;

/// <summary>
/// Hears the capture runs counter directly. The queue's reader starts with the execution context flow suppressed, so the
/// trace-matching telemetry capture never hears it. It hears every capture in the process, so every class that drives the
/// processor, the queue or the middleware shares the non-parallel recall telemetry collection.
/// </summary>
internal sealed class RunCounter : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _seen = new();
    private readonly System.Diagnostics.Metrics.MeterListener _listener = new();

    public RunCounter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "mnemosyne.capture.runs")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? outcome = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "mnemosyne.outcome")
                {
                    outcome = tag.Value as string;
                }
                else if (tag.Key == "mnemosyne.skip_reason")
                {
                    reason = tag.Value as string;
                }
            }

            _seen.Enqueue(reason is null ? outcome! : $"{outcome}/{reason}");
        });
        _listener.Start();
    }

    public IReadOnlyList<string> Seen => [.. _seen];

    public void Dispose() => _listener.Dispose();
}
