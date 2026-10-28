using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for the default <see cref="ReasonletSelector"/>.</summary>
/// <remarks>
/// The total score is <c>SimilarityWeight * similarity + SuccessRateWeight * successRate + FreshnessWeight * freshness</c>,
/// so the three weights must sum to 1.
/// </remarks>
public sealed class ReasonletRankingOptions
{
    private const double WeightSumTolerance = 1e-9;

    /// <summary>Gets the weight of the similarity in the total, in [0, 1]. Defaults to 0.5.</summary>
    public double SimilarityWeight { get; init; } = 0.5;

    /// <summary>Gets the weight of the smoothed success rate in the total, in [0, 1]. Defaults to 0.3.</summary>
    public double SuccessRateWeight { get; init; } = 0.3;

    /// <summary>Gets the weight of the freshness in the total, in [0, 1]. Defaults to 0.2.</summary>
    public double FreshnessWeight { get; init; } = 0.2;

    /// <summary>
    /// Gets the similarity below which a candidate is dropped, in [0, 1]. A query can override it. Defaults to 0.5.
    /// Similarity scales differ between embedding models, so tune this for yours.
    /// </summary>
    public double MinSimilarity { get; init; } = 0.5;

    /// <summary>
    /// Gets how many pseudo-runs the prior is worth when smoothing the success rate. Must be finite and above 0.
    /// Defaults to 5.
    /// </summary>
    public double PriorWeight { get; init; } = 5.0;

    /// <summary>
    /// Gets how much a run that only the agent judged counts toward the success rate, against a full 1 for a run that an
    /// evaluator or a human judged. In [0, 1], and not part of the three weights that must sum to 1. Defaults to 0.5. At 1
    /// every run counts in full; at 0 the rate rests on judged runs only.
    /// </summary>
    public double AgentVerdictWeight { get; init; } = 0.5;

    /// <summary>Gets the time in which freshness halves. Must be positive. Defaults to 30 days.</summary>
    public TimeSpan FreshnessHalfLife { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.UnitInterval(SimilarityWeight, nameof(SimilarityWeight)),
        () => Check.UnitInterval(SuccessRateWeight, nameof(SuccessRateWeight)),
        () => Check.UnitInterval(FreshnessWeight, nameof(FreshnessWeight)),
        () => Math.Abs(SimilarityWeight + SuccessRateWeight + FreshnessWeight - 1.0) <= WeightSumTolerance
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(nameof(SimilarityWeight), "the three weights must sum to 1"),
        () => Check.UnitInterval(MinSimilarity, nameof(MinSimilarity)),
        () => double.IsFinite(PriorWeight) && PriorWeight > 0.0
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(nameof(PriorWeight), "must be a finite number above 0"),
        () => Check.UnitInterval(AgentVerdictWeight, nameof(AgentVerdictWeight)),
        () => FreshnessHalfLife > TimeSpan.Zero
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(nameof(FreshnessHalfLife), "must be positive"));
}
