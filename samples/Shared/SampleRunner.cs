using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.Samples;

/// <summary>A call the sample depends on failed; the message says which.</summary>
public sealed class SampleFailure(string message) : Exception(message);

/// <summary>
/// Runs a sample as a check: it bounds the run with a time limit, turns every way the run can fail into a
/// <c>FAILED:</c> line and exit code 1, and holds the helpers the samples use to check their own receipts.
/// </summary>
public static class SampleRunner
{
    /// <summary>
    /// Runs <paramref name="body"/> under a token that is cancelled after <paramref name="limit"/>, and returns its exit code.
    /// A <see cref="SampleFailure"/>, a <see cref="TimeoutException"/>, the limit or any other exception writes a
    /// <c>FAILED:</c> line to <paramref name="error"/> and returns 1, so a sample never ends in a crash.
    /// </summary>
    public static async Task<int> RunAsync(Func<CancellationToken, Task<int>> body, TimeSpan limit, TextWriter error)
    {
        using var cts = new CancellationTokenSource(limit);
        try
        {
            return await body(cts.Token);
        }
        catch (SampleFailure failure)
        {
            error.WriteLine($"FAILED: {failure.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            error.WriteLine($"FAILED: the sample did not finish within {limit.TotalMinutes} minutes.");
            return 1;
        }
        catch (TimeoutException ex)
        {
            error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Runs <paramref name="walkthrough"/>, then erases the scope whatever happened, so a run leaves no Reasonlets behind.
    /// A failed erase is reported and turns a passing run into exit code 1, but never replaces a failure that is already on its way out.
    /// </summary>
    public static async Task<int> EraseAfterAsync(
        IReasonletMemory memory, ReasonletScope scope, TextWriter output, TextWriter error, Func<Task<int>> walkthrough)
    {
        var exitCode = 1;
        try
        {
            exitCode = await walkthrough();
        }
        finally
        {
            if (!await TryEraseAsync(memory, scope, output, error))
            {
                exitCode = 1;
            }
        }

        return exitCode;
    }

    // Runs in a finally block, so it must not throw: an exception here would hide the one that got the sample there.
    // The erase does not take the run's token, so a timed-out run still cleans up.
    /// <summary>Erases the scope and reports whether that worked; a failed or throwing erase is reported, never thrown.</summary>
    public static async Task<bool> TryEraseAsync(IReasonletMemory memory, ReasonletScope scope, TextWriter output, TextWriter error)
    {
        try
        {
            var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), CancellationToken.None);
            if (erased.IsFailure)
            {
                error.WriteLine($"Erase failed: {erased.Error.Code}: {erased.Error.Message}");
                return false;
            }

            output.WriteLine($"Erased the scope: {erased.Value.Reasonlets} Reasonlets and {erased.Value.Executions} executions deleted.");
            return true;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Erase threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns the value of a success, and throws a <see cref="SampleFailure"/> naming <paramref name="what"/> for a failure.</summary>
    public static T Require<T>(Result<T, MnemosyneError> result, string what) =>
        result.IsSuccess ? result.Value : throw new SampleFailure($"{what} failed: {result.Error.Code}: {result.Error.Message}");

    /// <summary>Throws a <see cref="SampleFailure"/> naming <paramref name="what"/> when the result is a failure.</summary>
    public static void RequireSuccess(UnitResult<MnemosyneError> result, string what)
    {
        if (result.IsFailure)
        {
            throw new SampleFailure($"{what} failed: {result.Error.Code}: {result.Error.Message}");
        }
    }

    /// <summary>Throws a <see cref="SampleFailure"/> naming <paramref name="what"/> when the two values differ.</summary>
    public static void RequireEqual<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new SampleFailure($"{what} was {actual}, not {expected}.");
        }
    }
}
