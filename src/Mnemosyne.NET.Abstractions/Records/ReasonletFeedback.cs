using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Caller feedback about a run of a Reasonlet: three scores, each in the unit interval, and an optional verdict.</summary>
/// <param name="Utility">How useful the Reasonlet was.</param>
/// <param name="Correctness">How correct the Reasonlet was.</param>
/// <param name="Reusability">How reusable the Reasonlet is.</param>
/// <param name="Worked">
/// A human's optional verdict on whether the run worked. When present, it is the run's verdict and overrides both an
/// evaluator's and the agent's own. Feedback recorded later with <see langword="null"/> keeps an earlier verdict, so a
/// verdict can be changed, by recording the other value, but not retracted.
/// </param>
public readonly record struct ReasonletFeedback(double Utility, double Correctness, double Reusability, bool? Worked = null)
{
    /// <summary>Validates the feedback. The default value is valid, and so is any <see cref="Worked"/>.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var utility = Utility;
        var correctness = Correctness;
        var reusability = Reusability;
        return Check.All(
            () => Check.UnitInterval(utility, nameof(Utility)),
            () => Check.UnitInterval(correctness, nameof(Correctness)),
            () => Check.UnitInterval(reusability, nameof(Reusability)));
    }
}
