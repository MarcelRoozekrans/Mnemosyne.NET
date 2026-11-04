using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>The validation rule every timeout option shares.</summary>
internal static class TimeoutCheck
{
    /// <summary>
    /// The longest finite timeout, about 49.7 days: the longest delay a <see cref="CancellationTokenSource"/> accepts.
    /// </summary>
    internal static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>Fails unless a timeout is positive and at most <see cref="Max"/>, or <see cref="Timeout.InfiniteTimeSpan"/>.</summary>
    internal static UnitResult<MnemosyneError> Positive(TimeSpan timeout, string path) =>
        (timeout > TimeSpan.Zero && timeout <= Max) || timeout == Timeout.InfiniteTimeSpan
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(path, "must be positive and at most 4294967294 milliseconds, or Timeout.InfiniteTimeSpan");
}
