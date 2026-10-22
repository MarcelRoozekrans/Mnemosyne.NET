using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The intent a Reasonlet serves, with the confidence in that classification.</summary>
/// <param name="Intent">The intent text. Must not be blank.</param>
/// <param name="Confidence">Confidence in the unit interval.</param>
public readonly record struct ReasonletIntent(string Intent, double Confidence)
{
    /// <summary>Validates the intent.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var intent = Intent;
        var confidence = Confidence;
        return Check.All(
            () => Check.NotBlank(intent, nameof(Intent)),
            () => Check.UnitInterval(confidence, nameof(Confidence)));
    }
}
