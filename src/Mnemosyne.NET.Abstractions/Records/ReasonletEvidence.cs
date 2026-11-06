using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>
/// The evidence behind a match's <see cref="ReasonletScore.SuccessRate"/>, so a caller can tell a rate earned over many
/// runs from one that rests on a handful of runs and an inherited prior.
/// </summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public readonly record struct ReasonletEvidence
{
    /// <summary>Gets the Reasonlet's own number of runs. Must be at least 0.</summary>
    public required int Runs { get; init; }

    /// <summary>
    /// Gets how many of those runs an evaluator or a human judged; the rest carry only the agent's own verdict. Between 0 and
    /// <see cref="Runs"/>.
    /// </summary>
    public required int JudgedRuns { get; init; }

    /// <summary>
    /// Gets whether the rate was smoothed toward the parent's rate. With the default selector that is the case exactly when the
    /// Reasonlet has a parent whose smoothed rate is above the neutral 0.5; otherwise the rate was smoothed toward 0.5.
    /// </summary>
    public required bool UsesParentPrior { get; init; }

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
