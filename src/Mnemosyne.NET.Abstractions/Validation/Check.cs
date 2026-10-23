using System.Globalization;
using System.Text;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions.Validation;

/// <summary>
/// Validation helpers that return <see cref="MnemosyneErrorCode.Validation"/> failures whose message reads
/// <c>path: reason</c>, the form every <c>Validate</c> method in Mnemosyne uses.
/// </summary>
/// <remarks>
/// Packages that extend Mnemosyne, such as an options type or a store, use them so their validation failures read the
/// same way. Each check returns success or one failure; <see cref="All"/> runs a sequence and stops at the first failure,
/// and <see cref="Nested"/> prefixes a nested record's path.
/// </remarks>
public static class Check
{
    private static UnitResult<MnemosyneError> Ok() => UnitResult<MnemosyneError>.Success();

    /// <summary>Creates a validation failure for a path.</summary>
    /// <param name="path">The path of the invalid value, such as <c>Ranking.MinSimilarity</c>.</param>
    /// <param name="reason">Why the value is invalid, such as <c>must be positive</c>. It must not quote user data.</param>
    /// <returns>A <see cref="MnemosyneErrorCode.Validation"/> failure whose message is <c>path: reason</c>.</returns>
    public static UnitResult<MnemosyneError> Fail(string path, string reason) =>
        UnitResult<MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Validation, $"{path}: {reason}"));

    internal static UnitResult<MnemosyneError> NotBlank(string? value, string path) =>
        string.IsNullOrWhiteSpace(value) ? Fail(path, "must not be blank") : Ok();

    /// <summary>Fails when a non-null string holds a lone surrogate, which cannot be written as UTF-8 without changing it.</summary>
    internal static UnitResult<MnemosyneError> ValidUtf16(string? value, string path) =>
        value is null || IsValidUtf16(value) ? Ok() : Fail(path, "must be valid UTF-16");

    /// <summary>Returns whether a string has no lone surrogate.</summary>
    internal static bool IsValidUtf16(string value)
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

    /// <summary>
    /// Fails when a non-null string has more than <paramref name="max"/> Unicode code points, the unit JSON Schema
    /// <c>maxLength</c> counts. The string must already be valid UTF-16.
    /// </summary>
    internal static UnitResult<MnemosyneError> MaxLength(string? value, int max, string path) =>
        value is null || CodePointCount(value) <= max ? Ok() : Fail(path, $"must not be longer than {max} code points");

    /// <summary>Returns the number of code points in valid UTF-16 text: a surrogate pair counts once.</summary>
    internal static int CodePointCount(string value)
    {
        var count = value.Length;
        foreach (var c in value)
        {
            if (char.IsLowSurrogate(c))
            {
                count--;
            }
        }

        return count;
    }

    /// <summary>Fails unless a value is a finite number from 0 to 1, both included.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="path">The path of the value.</param>
    /// <returns>Success, or a validation failure for <paramref name="path"/>.</returns>
    public static UnitResult<MnemosyneError> UnitInterval(double value, string path) =>
        double.IsFinite(value) && value >= 0.0 && value <= 1.0 ? Ok() : Fail(path, "must be a finite number between 0 and 1");

    /// <summary>Fails unless a value is from <paramref name="min"/> to <paramref name="max"/>, both included.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="min">The smallest allowed value.</param>
    /// <param name="max">The largest allowed value.</param>
    /// <param name="path">The path of the value.</param>
    /// <returns>Success, or a validation failure for <paramref name="path"/>.</returns>
    public static UnitResult<MnemosyneError> Range(int value, int min, int max, string path) =>
        value >= min && value <= max ? Ok() : Fail(path, $"must be between {min} and {max}");

    internal static UnitResult<MnemosyneError> NotNegative(TimeSpan value, string path) =>
        value >= TimeSpan.Zero ? Ok() : Fail(path, "must not be negative");

    internal static UnitResult<MnemosyneError> PositiveWhenPresent(TimeSpan? value, string path) =>
        value is not { } span || span > TimeSpan.Zero ? Ok() : Fail(path, "must be positive");

    /// <summary>Fails when a Reasonlet id is the <c>default</c> value, which identifies nothing.</summary>
    /// <param name="id">The id to check.</param>
    /// <param name="path">The path of the id.</param>
    /// <returns>Success, or a validation failure for <paramref name="path"/>.</returns>
    public static UnitResult<MnemosyneError> NotDefault(ReasonletId id, string path) =>
        id.IsDefault ? Fail(path, "must not be the default id") : Ok();

    /// <summary>Fails when an execution id is the <c>default</c> value, which identifies nothing.</summary>
    /// <param name="id">The id to check.</param>
    /// <param name="path">The path of the id.</param>
    /// <returns>Success, or a validation failure for <paramref name="path"/>.</returns>
    public static UnitResult<MnemosyneError> NotDefault(ExecutionId id, string path) =>
        id.IsDefault ? Fail(path, "must not be the default id") : Ok();

    internal static UnitResult<MnemosyneError> Defined<TEnum>(TEnum value, string path)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? Ok() : Fail(path, "must be a defined enum value");

    internal static UnitResult<MnemosyneError> NotEmpty(ReadOnlyMemory<float> value, string path) =>
        value.IsEmpty ? Fail(path, "must not be empty") : Ok();

    internal static UnitResult<MnemosyneError> AllFinite(ReadOnlyMemory<float> value, string path)
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

    internal static UnitResult<MnemosyneError> MatchesWhenPresent(string value, string? other, string path, string otherPath) =>
        other is null || string.Equals(value, other, StringComparison.Ordinal) ? Ok() : Fail(path, $"must equal {otherPath} when that is set");

    /// <summary>Fails when a value is null.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="path">The path of the value.</param>
    /// <returns>Success, or a validation failure for <paramref name="path"/>.</returns>
    public static UnitResult<MnemosyneError> NotNull(object? value, string path) =>
        value is null ? Fail(path, "must not be null") : Ok();

    /// <summary>
    /// Prefixes a nested record's validation failure with the path of that record, so <c>Top: must be between 1 and 50</c> from a
    /// query held in <c>Query</c> reads <c>Query.Top: must be between 1 and 50</c>. Success and failures of any other code pass through unchanged.
    /// </summary>
    /// <param name="inner">The nested record's validation result.</param>
    /// <param name="prefix">The path of the nested record.</param>
    /// <returns>The result, with the path prefixed when it is a validation failure.</returns>
    public static UnitResult<MnemosyneError> Nested(UnitResult<MnemosyneError> inner, string prefix) =>
        inner.IsSuccess || inner.Error.Code != MnemosyneErrorCode.Validation
            ? inner
            : UnitResult<MnemosyneError>.Failure(inner.Error with { Message = $"{prefix}.{inner.Error.Message}" });

    /// <summary>Runs checks in order and returns the first failure, without running the checks after it.</summary>
    /// <param name="checks">The checks. Each is a delegate, so a later check can rely on an earlier one having passed.</param>
    /// <returns>Success when every check passes, else the first failure.</returns>
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
    internal static string KeyPath(string prefix, string? key)
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
