using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for <see cref="ReasonletMemory"/> and the default <see cref="ReasonletSelector"/>.</summary>
public sealed class MnemosyneOptions
{
    private const int MaxCandidatePool = 200;

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

    /// <summary>Gets the settings of the default selector. Must not be null and must be valid.</summary>
    public ReasonletRankingOptions Ranking { get; init; } = new();

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property; the ranking's paths nest under <c>Ranking</c>.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Range(CandidatePool, 1, MaxCandidatePool, nameof(CandidatePool)),
        () => Check.UnitInterval(MinIntentConfidence, nameof(MinIntentConfidence)),
        () => Check.NotNull(Ranking, nameof(Ranking)),
        () => Check.Nested(Ranking.Validate(), nameof(Ranking)));
}
