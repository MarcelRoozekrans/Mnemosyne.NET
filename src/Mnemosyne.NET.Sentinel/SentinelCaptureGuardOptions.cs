using AI.Sentinel.Detection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Sentinel;

/// <summary>Settings for the AI.Sentinel capture guard.</summary>
public sealed class SentinelCaptureGuardOptions
{
    /// <summary>
    /// Gets the lowest detector severity that blocks a capture. Must be Low, Medium, High or Critical. Defaults to
    /// <see cref="Severity.High"/>.
    /// </summary>
    public Severity BlockAtSeverity { get; init; } = Severity.High;

    /// <summary>
    /// Gets the name of the AI.Sentinel pipeline whose detectors run, so a host can give the guard its own. Must not be
    /// blank when set. Defaults to null, which uses the default pipeline.
    /// </summary>
    public string? PipelineName { get; init; }

    /// <summary>
    /// Gets the name the guard reports itself under. Must not be blank, must be valid UTF-16 and must not be longer than
    /// <see cref="EvaluationLimits.MaxEvaluatorLength"/> code points. Defaults to <c>ai-sentinel</c>.
    /// </summary>
    public string GuardName { get; init; } = "ai-sentinel";

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Enum.IsDefined(BlockAtSeverity) && BlockAtSeverity != Severity.None
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(nameof(BlockAtSeverity), "must be Low, Medium, High or Critical"),
        () => PipelineName is null
            ? UnitResult<MnemosyneError>.Success()
            : Check.NotBlank(PipelineName, nameof(PipelineName)),
        () => Check.NotBlank(GuardName, nameof(GuardName)),
        () => Check.ValidUtf16(GuardName, nameof(GuardName)),
        () => Check.MaxLength(GuardName, EvaluationLimits.MaxEvaluatorLength, nameof(GuardName)));
}
