using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for the background capture that <c>AddReasonletCapture</c> registers.</summary>
public sealed class ReasonletCaptureOptions
{
    private const int MaxCapacity = 100_000;

    /// <summary>Gets how many captures may wait in the queue, from 1 to 100000. Defaults to 256.</summary>
    public int Capacity { get; init; } = 256;

    /// <summary>Gets the source recorded on captured transcripts. Must not be blank. Defaults to <c>mnemosyne.chat</c>.</summary>
    public string Source { get; init; } = "mnemosyne.chat";

    /// <summary>
    /// Gets how long shutdown waits for queued captures. Must be positive and at most 4,294,967,294 milliseconds, or
    /// <see cref="Timeout.InfiniteTimeSpan"/>. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Range(Capacity, 1, MaxCapacity, nameof(Capacity)),
        () => Check.NotBlank(Source, nameof(Source)),
        () => TimeoutCheck.Positive(DrainTimeout, nameof(DrainTimeout)));
}
