using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>
/// The evidence behind a match's <see cref="ReasonletScore.SuccessRate"/>, so a caller can tell a rate earned over many
/// runs from one that rests on a handful of runs and an inherited prior.
/// </summary>
/// <param name="Runs">The Reasonlet's own number of runs. Must be at least 0.</param>
/// <param name="JudgedRuns">
/// How many of those runs an evaluator or a human judged; the rest carry only the agent's own verdict. Between 0 and
/// <paramref name="Runs"/>.
/// </param>
/// <param name="UsesParentPrior">
/// Whether the rate was smoothed toward the parent's rate. With the default selector that is the case exactly when the
/// Reasonlet has a parent whose smoothed rate is above the neutral 0.5; otherwise the rate was smoothed toward 0.5.
/// </param>
public readonly record struct ReasonletEvidence(int Runs, int JudgedRuns, bool UsesParentPrior)
{
    /// <summary>Validates the evidence. The default value is valid.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var runs = Runs;
        var judgedRuns = JudgedRuns;
        return Check.All(
            () => Check.Range(runs, 0, int.MaxValue, nameof(Runs)),
            () => Check.Range(judgedRuns, 0, runs, nameof(JudgedRuns)));
    }
}
