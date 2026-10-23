using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Caller feedback about a Reasonlet, each value in the unit interval.</summary>
/// <param name="Utility">How useful the Reasonlet was.</param>
/// <param name="Correctness">How correct the Reasonlet was.</param>
/// <param name="Reusability">How reusable the Reasonlet is.</param>
public readonly record struct ReasonletFeedback(double Utility, double Correctness, double Reusability)
{
    /// <summary>Validates the feedback. The default value is valid.</summary>
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
