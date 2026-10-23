using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Ranking score of a recalled Reasonlet, each component in the unit interval.</summary>
/// <param name="Similarity">Similarity to the query.</param>
/// <param name="SuccessRate">
/// The success rate of the Reasonlet's runs, smoothed toward a prior so a handful of runs cannot swing it to 0 or 1. With
/// the default selector the prior is the parent's smoothed rate when that is better than neutral, and 0.5 otherwise.
/// </param>
/// <param name="Freshness">
/// How recently the Reasonlet last ran: 1 for a run just now, halving with every half-life since. With the default selector
/// that is <c>0.5 ^ (age / FreshnessHalfLife)</c>, the age counted from <see cref="ReasonletMetrics.LastExecutedAt"/>.
/// </param>
/// <param name="Total">The combined score.</param>
public readonly record struct ReasonletScore(double Similarity, double SuccessRate, double Freshness, double Total)
{
    /// <summary>Validates the score. The default value is valid.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var similarity = Similarity;
        var successRate = SuccessRate;
        var freshness = Freshness;
        var total = Total;
        return Check.All(
            () => Check.UnitInterval(similarity, nameof(Similarity)),
            () => Check.UnitInterval(successRate, nameof(SuccessRate)),
            () => Check.UnitInterval(freshness, nameof(Freshness)),
            () => Check.UnitInterval(total, nameof(Total)));
    }
}
