using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A capture guard's verdict on one capture: allowed, or blocked by a named detector.</summary>
/// <remarks>
/// The verdict never quotes the capture. <see cref="Guard"/> and <see cref="Detector"/> name the guard and the rule that
/// fired, and they appear in telemetry and in the error returned for a blocked capture. The default value is invalid.
/// </remarks>
public readonly record struct CaptureVerdict
{
    private CaptureVerdict(string guard, string? detector, bool isBlocked)
    {
        Guard = guard;
        Detector = detector;
        IsBlocked = isBlocked;
    }

    /// <summary>Whether the guard blocked the capture.</summary>
    public bool IsBlocked { get; }

    /// <summary>
    /// A short identifier of the guard, such as <c>redactor</c>. Must not be blank and must not be longer than 100 code
    /// points.
    /// </summary>
    public string Guard { get; }

    /// <summary>
    /// A short identifier of the detector that fired, such as <c>pii.email</c>. Set exactly when the capture is blocked.
    /// Must not be blank and must not be longer than 100 code points.
    /// </summary>
    public string? Detector { get; }

    /// <summary>Creates a verdict that lets the capture through. This does not validate the argument; <see cref="Validate"/> does.</summary>
    /// <param name="guard">The guard's identifier.</param>
    /// <returns>The verdict.</returns>
    public static CaptureVerdict Allow(string guard) => new(guard, null, false);

    /// <summary>Creates a verdict that blocks the capture. This does not validate the arguments; <see cref="Validate"/> does.</summary>
    /// <param name="guard">The guard's identifier.</param>
    /// <param name="detector">The identifier of the detector that fired.</param>
    /// <returns>The verdict.</returns>
    public static CaptureVerdict Block(string guard, string detector) => new(guard, detector, true);

    /// <summary>Validates the verdict. The default value is invalid, because it names no guard.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var guard = Guard;
        var detector = Detector;
        var blocked = IsBlocked;
        return Check.All(
            () => Check.NotBlank(guard, nameof(Guard)),
            () => Check.ValidUtf16(guard, nameof(Guard)),
            () => Check.MaxLength(guard, EvaluationLimits.MaxEvaluatorLength, nameof(Guard)),
            () => !blocked ? UnitResult<MnemosyneError>.Success() : Check.All(
                () => Check.NotBlank(detector, nameof(Detector)),
                () => Check.ValidUtf16(detector!, nameof(Detector)),
                () => Check.MaxLength(detector!, EvaluationLimits.MaxEvaluatorLength, nameof(Detector))));
    }
}
