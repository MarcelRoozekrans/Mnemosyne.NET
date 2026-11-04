using System.Threading.Channels;

namespace Mnemosyne.NET;

/// <summary>
/// The bounded queue between the chat middleware and the background capture: the middleware offers each finished call
/// with <see cref="TryEnqueue"/>, which never waits, and one reader hands the items to the processor one at a time.
/// </summary>
/// <remarks>
/// <para>
/// The reader starts on the first accepted item, so a host without the generic host still captures. It runs outside the
/// execution context of the call that started it, so it neither joins that call's trace nor keeps its async-local state.
/// </para>
/// <para>
/// <see cref="DrainAsync"/> closes the queue and waits for the reader to work through what is left. When its token fires
/// first, the reader's token is cancelled: the item in progress is cancelled and the rest is skipped. Every item that does
/// not finish is counted once as <c>dropped_shutdown</c>. Stopping the host drains through
/// <see cref="ReasonletCaptureHostedService"/>; disposing drains bounded by <see cref="ReasonletCaptureOptions.DrainTimeout"/>.
/// There is one drain: a later call waits for it, and its token can cut it short too.
/// </para>
/// </remarks>
internal sealed class ReasonletCaptureQueue : IAsyncDisposable
{
    private readonly ReasonletCaptureProcessor _processor;
    private readonly TimeSpan _drainTimeout;
    private readonly Channel<ReasonletCaptureItem> _channel;

    // Not disposed: it has no timer, and a drain token that fires after disposal would otherwise cancel a disposed source.
    private readonly CancellationTokenSource _stop = new();

    // The reader, created once by whichever comes first: the first accepted item, or the drain. Lazy's lock makes that
    // one decision, so an item accepted just before the queue closed either has a reader the drain waits for, or is
    // still in the channel when the drain looks.
    private readonly Lazy<Task> _reader;
    private readonly Lazy<Task> _drain;
    private volatile bool _closing;
    private int _dropped;

    /// <summary>Creates the queue. No reader runs until the first item is accepted.</summary>
    /// <param name="processor">Processes each item.</param>
    /// <param name="options">The capture options, for the capacity and the drain timeout.</param>
    public ReasonletCaptureQueue(ReasonletCaptureProcessor processor, ReasonletCaptureOptions options)
    {
        _processor = processor;
        _drainTimeout = options.DrainTimeout;
        _channel = Channel.CreateBounded<ReasonletCaptureItem>(new BoundedChannelOptions(options.Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _reader = new Lazy<Task>(StartReader);
        _drain = new Lazy<Task>(DrainCoreAsync);
    }

    /// <summary>Offers one item without waiting, and counts the outcome.</summary>
    /// <param name="item">The finished call.</param>
    /// <returns>True when the item was accepted; false when the queue is full or closed.</returns>
    internal bool TryEnqueue(ReasonletCaptureItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            CaptureTelemetry.Queue(_closing ? CaptureTelemetry.DroppedShutdownOutcome : CaptureTelemetry.DroppedFullOutcome);
            return false;
        }

        CaptureTelemetry.Queue(CaptureTelemetry.EnqueuedOutcome);
        _ = _reader.Value;
        return true;
    }

    /// <summary>Closes the queue and waits for the reader; <paramref name="ct"/> cancels the work that is left.</summary>
    /// <param name="ct">Cancels the reader's work: the item in progress and every item behind it.</param>
    /// <returns>A task that completes when the reader has stopped and the leftovers are counted.</returns>
    internal Task DrainAsync(CancellationToken ct)
    {
        var drain = _drain.Value;
        return drain.IsCompleted ? drain : WaitAsync(drain, ct);
    }

    /// <summary>Drains, giving up on the work that is left after <see cref="ReasonletCaptureOptions.DrainTimeout"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(_drainTimeout);
        await DrainAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task WaitAsync(Task drain, CancellationToken ct)
    {
        using var registration = ct.Register(static stop => ((CancellationTokenSource)stop!).Cancel(), _stop);
        await drain.ConfigureAwait(false);
    }

    private async Task DrainCoreAsync()
    {
        _closing = true;
        _channel.Writer.TryComplete();
        await _reader.Value.ConfigureAwait(false);
        for (var i = 0; i < _dropped; i++)
        {
            CaptureTelemetry.Queue(CaptureTelemetry.DroppedShutdownOutcome);
        }
    }

    /// <summary>Starts the reader on the thread pool, unless the queue is already closed and empty.</summary>
    private Task StartReader()
    {
        if (_channel.Reader.Completion.IsCompleted)
        {
            return Task.CompletedTask;
        }

        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(ReadAsync);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(ReadAsync);
        }
    }

    /// <summary>Processes items one at a time until the queue is closed and empty; after a stop, counts the rest as dropped.</summary>
    private async Task ReadAsync()
    {
        var reader = _channel.Reader;
        var token = _stop.Token;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                if (token.IsCancellationRequested)
                {
                    _dropped++;
                    continue;
                }

                try
                {
                    await _processor.ProcessAsync(item, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The processor throws this only when the stop token is cancelled: the item did not finish.
                    _dropped++;
                }
            }
        }
    }
}
