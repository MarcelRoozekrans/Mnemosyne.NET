using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for the recall step of the chat middleware and the agent provider.</summary>
public sealed class ReasonletSuggestionOptions
{
    /// <summary>
    /// Gets the lowest similarity, in [0, 1], a recalled Reasonlet needs to be suggested. Defaults to null, which keeps the
    /// selector's own threshold.
    /// </summary>
    public double? MinSimilarity { get; init; }

    /// <summary>
    /// Gets how long a recall may take before it is abandoned and nothing is suggested. Must be positive and at most
    /// 4,294,967,294 milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/>. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan RecallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how a recalled match is written as a suggestion. Defaults to null, which uses the built-in format.</summary>
    public Func<ReasonletMatch, string>? Formatter { get; init; }

    /// <summary>Gets the clock for the recall timeout. Defaults to <see cref="TimeProvider.System"/>; it must not be null.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => ValidateRecall(MinSimilarity, RecallTimeout),
        () => Check.NotNull(TimeProvider, nameof(TimeProvider)));

    /// <summary>The rules every recall setting shares: the similarity floor is in [0, 1] and the timeout is valid.</summary>
    internal static UnitResult<MnemosyneError> ValidateRecall(double? minSimilarity, TimeSpan recallTimeout) => Check.All(
        () => minSimilarity is { } min ? Check.UnitInterval(min, nameof(MinSimilarity)) : UnitResult<MnemosyneError>.Success(),
        () => TimeoutCheck.Positive(recallTimeout, nameof(RecallTimeout)));
}
