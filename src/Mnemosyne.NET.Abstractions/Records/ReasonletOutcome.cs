using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The result of one execution of a Reasonlet.</summary>
/// <param name="Success">Whether the execution succeeded.</param>
/// <param name="Duration">How long the execution took. Must not be negative.</param>
/// <param name="Score">An optional quality score in the unit interval.</param>
public readonly record struct ReasonletOutcome(bool Success, TimeSpan Duration, double? Score)
{
    /// <summary>Validates the outcome. The default value is valid.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var duration = Check.NotNegative(Duration, nameof(Duration));
        if (duration.IsFailure)
        {
            return duration;
        }

        return Score is { } score ? Check.UnitInterval(score, nameof(Score)) : UnitResult<MnemosyneError>.Success();
    }
}
