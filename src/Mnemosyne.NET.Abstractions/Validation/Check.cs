using System.Globalization;
using System.Text;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Validation helpers that return <see cref="MnemosyneErrorCode.Validation"/> failures.</summary>
internal static class Check
{
    private static UnitResult<MnemosyneError> Ok() => UnitResult<MnemosyneError>.Success();

    public static UnitResult<MnemosyneError> Fail(string path, string reason) =>
        UnitResult<MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Validation, $"{path}: {reason}"));

    public static UnitResult<MnemosyneError> NotBlank(string? value, string path) =>
        string.IsNullOrWhiteSpace(value) ? Fail(path, "must not be blank") : Ok();

    /// <summary>Fails when a non-null string holds a lone surrogate, which cannot be written as UTF-8 without changing it.</summary>
    public static UnitResult<MnemosyneError> ValidUtf16(string? value, string path) =>
        value is null || IsValidUtf16(value) ? Ok() : Fail(path, "must be valid UTF-16");

    /// <summary>Returns whether a string has no lone surrogate.</summary>
    public static bool IsValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

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

    public static UnitResult<MnemosyneError> Defined<TEnum>(TEnum value, string path)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? Ok() : Fail(path, "must be a defined enum value");

    public static UnitResult<MnemosyneError> NotEmpty(ReadOnlyMemory<float> value, string path) =>
        value.IsEmpty ? Fail(path, "must not be empty") : Ok();

    public static UnitResult<MnemosyneError> AllFinite(ReadOnlyMemory<float> value, string path)
    {
        var span = value.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (!float.IsFinite(span[i]))
            {
                return Fail($"{path}[{i}]", "must be a finite number");
            }
        }

        return Ok();
    }

    public static UnitResult<MnemosyneError> MatchesWhenPresent(string value, string? other, string path, string otherPath) =>
        other is null || string.Equals(value, other, StringComparison.Ordinal) ? Ok() : Fail(path, $"must equal {otherPath} when that is set");

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

    /// <summary>
    /// Builds an indexer path such as <c>Tags["k"]</c>, escaping backslash, quote, control characters and lone
    /// surrogates in the key, so the path is always valid UTF-16.
    /// </summary>
    public static string KeyPath(string prefix, string? key)
    {
        if (key is null)
        {
            return $"{prefix}[null]";
        }

        var builder = new StringBuilder(prefix.Length + key.Length + 4);
        builder.Append(prefix).Append("[\"");
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsHighSurrogate(c) && i + 1 < key.Length && char.IsLowSurrogate(key[i + 1]))
            {
                builder.Append(c).Append(key[++i]);
            }
            else if (c == '\\')
            {
                builder.Append("\\\\");
            }
            else if (c == '"')
            {
                builder.Append("\\\"");
            }
            else if (char.IsControl(c) || char.IsSurrogate(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.Append("\"]").ToString();
    }
}
