using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for <see cref="ReasonletMemory"/> and the default <see cref="ReasonletSelector"/>.</summary>
public sealed class MnemosyneOptions
{
    private const int MaxCandidatePool = 200;

    /// <summary>
    /// The longest finite <see cref="EvaluationTimeout"/>, about 49.7 days: the longest delay a
    /// <see cref="CancellationTokenSource"/> accepts.
    /// </summary>
    internal static readonly TimeSpan MaxEvaluationTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>
    /// Gets how many candidates recall asks the store for before ranking, from 1 to 200. A query whose
    /// <see cref="ReasonletQuery.Top"/> is larger raises it to <see cref="ReasonletQuery.Top"/>. Defaults to 20.
    /// </summary>
    /// <remarks>A pool larger than <see cref="ReasonletQuery.Top"/> lets reliability and freshness reorder results that similarity alone would cut.</remarks>
    public int CandidatePool { get; init; } = 20;

    /// <summary>
    /// Gets the confidence, in [0, 1], an extracted intent needs before recall filters on it. Defaults to 0.7.
    /// Remember stores the extracted intent whatever its confidence.
    /// </summary>
    /// <remarks>A wrong filter hides every right answer, so recall filters only on an intent it is confident about.</remarks>
    public double MinIntentConfidence { get; init; } = 0.7;

    /// <summary>
    /// Gets how long the facade waits for an <see cref="IReasonletEvaluator"/> before it treats the judge as down.
    /// Must be positive and at most 4,294,967,294 milliseconds, about 49.7 days, the longest delay a
    /// <see cref="CancellationTokenSource"/> accepts, or <see cref="Timeout.InfiniteTimeSpan"/> to wait as long as the
    /// caller's token allows. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// When the time is up, the facade cancels the token it passes the evaluator and stops waiting, even for an evaluator
    /// that ignores the token. That call is abandoned: it may run on, but its outcome is discarded.
    /// A judge that times out is down: the run is stored with the agent's own verdict, and the judge call's telemetry shows
    /// outcome <c>Unavailable</c>. The caller's own cancellation still propagates.
    /// </remarks>
    public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long the facade waits for an <see cref="ICaptureGuard"/> before it treats the guard as down.
    /// Must be positive and at most 4,294,967,294 milliseconds, about 49.7 days, the longest delay a
    /// <see cref="CancellationTokenSource"/> accepts, or <see cref="Timeout.InfiniteTimeSpan"/> to wait as long as the
    /// caller's token allows. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// When the time is up, the facade cancels the token it passes the guard and stops waiting, even for a guard that
    /// ignores the token. That call is abandoned: it may run on, but its outcome is discarded.
    /// A guard that times out is down: the capture fails with the guard-down error. The caller's own cancellation still propagates.
    /// </remarks>
    public TimeSpan GuardTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the settings of the default selector. Must not be null and must be valid.</summary>
    public ReasonletRankingOptions Ranking { get; init; } = new();

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property; the ranking's paths nest under <c>Ranking</c>.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Range(CandidatePool, 1, MaxCandidatePool, nameof(CandidatePool)),
        () => Check.UnitInterval(MinIntentConfidence, nameof(MinIntentConfidence)),
        () => CheckTimeout(EvaluationTimeout, nameof(EvaluationTimeout)),
        () => CheckTimeout(GuardTimeout, nameof(GuardTimeout)),
        () => Check.NotNull(Ranking, nameof(Ranking)),
        () => Check.Nested(Ranking.Validate(), nameof(Ranking)));

    private static UnitResult<MnemosyneError> CheckTimeout(TimeSpan timeout, string path) =>
        (timeout > TimeSpan.Zero && timeout <= MaxEvaluationTimeout) || timeout == Timeout.InfiniteTimeSpan
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(path, "must be positive and at most 4294967294 milliseconds, or Timeout.InfiniteTimeSpan");
}
