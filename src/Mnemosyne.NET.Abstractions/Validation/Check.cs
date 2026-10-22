using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Validation helpers that return <see cref="MnemosyneErrorCode.Validation"/> failures.</summary>
internal static class Check
{
    private static UnitResult<MnemosyneError> Ok() => UnitResult<MnemosyneError>.Success();

    private static UnitResult<MnemosyneError> Fail(string path, string reason) =>
        UnitResult<MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Validation, $"{path}: {reason}"));

    public static UnitResult<MnemosyneError> NotBlank(string? value, string path) =>
        string.IsNullOrWhiteSpace(value) ? Fail(path, "must not be blank") : Ok();

    public static UnitResult<MnemosyneError> UnitInterval(double value, string path) =>
        double.IsFinite(value) && value >= 0.0 && value <= 1.0 ? Ok() : Fail(path, "must be a finite number between 0 and 1");

    public static UnitResult<MnemosyneError> Range(int value, int min, int max, string path) =>
        value >= min && value <= max ? Ok() : Fail(path, $"must be between {min} and {max}");

    public static UnitResult<MnemosyneError> NotNegative(TimeSpan value, string path) =>
        value >= TimeSpan.Zero ? Ok() : Fail(path, "must not be negative");

    public static UnitResult<MnemosyneError> NotDefault(ReasonletId id, string path) =>
        id.IsDefault ? Fail(path, "must not be the default id") : Ok();

    public static UnitResult<MnemosyneError> NotDefault(ExecutionId id, string path) =>
        id.IsDefault ? Fail(path, "must not be the default id") : Ok();

    public static UnitResult<MnemosyneError> NotNull(object? value, string path) =>
        value is null ? Fail(path, "must not be null") : Ok();

    public static UnitResult<MnemosyneError> Nested(UnitResult<MnemosyneError> inner, string prefix) =>
        inner.IsSuccess || inner.Error.Code != MnemosyneErrorCode.Validation
            ? inner
            : UnitResult<MnemosyneError>.Failure(inner.Error with { Message = $"{prefix}.{inner.Error.Message}" });

    public static UnitResult<MnemosyneError> All(params ReadOnlySpan<Func<UnitResult<MnemosyneError>>> checks)
    {
        foreach (var check in checks)
        {
            var result = check();
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Ok();
    }
}
