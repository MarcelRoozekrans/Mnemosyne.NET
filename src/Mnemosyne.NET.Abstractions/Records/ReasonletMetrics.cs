using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Aggregate execution statistics for a Reasonlet.</summary>
/// <param name="Runs">Total number of runs. Must be at least 0.</param>
/// <param name="Successes">Number of successful runs. Between 0 and <paramref name="Runs"/>.</param>
/// <param name="LastSuccessAt">When the last successful run happened, if any.</param>
/// <param name="LastExecutedAt">
/// When the latest run happened. The store keeps it with the run counters, so it survives the pruning of old executions.
/// It falls back to the time the Reasonlet was stored only for a Reasonlet that has no metrics row.
/// </param>
public readonly record struct ReasonletMetrics(int Runs, int Successes, DateTimeOffset? LastSuccessAt, DateTimeOffset LastExecutedAt)
{
    /// <summary>Validates the metrics. The default value is valid.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate()
    {
        var runs = Runs;
        var successes = Successes;
        return Check.All(
            () => Check.Range(runs, 0, int.MaxValue, nameof(Runs)),
            () => Check.Range(successes, 0, runs, nameof(Successes)));
    }
}
