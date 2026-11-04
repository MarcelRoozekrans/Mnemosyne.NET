using Microsoft.Extensions.Hosting;

namespace Mnemosyne.NET;

/// <summary>Drains the <see cref="ReasonletCaptureQueue"/> when the host stops.</summary>
/// <remarks>
/// Starting does nothing: the queue starts its reader on the first item, so capture works without a host too.
/// </remarks>
/// <param name="queue">The queue to drain.</param>
internal sealed class ReasonletCaptureHostedService(ReasonletCaptureQueue queue) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Drains the queue until it is empty or <paramref name="cancellationToken"/> fires.</summary>
    /// <param name="cancellationToken">Cancels the capture work that is left.</param>
    /// <returns>A task that completes when the queue's reader has stopped.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => queue.DrainAsync(cancellationToken);
}
