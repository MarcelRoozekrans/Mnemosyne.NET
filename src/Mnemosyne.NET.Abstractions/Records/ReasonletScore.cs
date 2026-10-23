using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Ranking score of a recalled Reasonlet, each component in the unit interval.</summary>
/// <param name="Similarity">Similarity to the query.</param>
/// <param name="SuccessRate">Historical success rate.</param>
/// <param name="Freshness">How recent the Reasonlet is.</param>
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
