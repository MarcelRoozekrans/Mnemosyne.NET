using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Aggregate execution statistics for a Reasonlet.</summary>
/// <param name="Runs">Total number of runs. Must be at least 0.</param>
/// <param name="Successes">Number of successful runs. Between 0 and <paramref name="Runs"/>.</param>
/// <param name="LastSuccessAt">When the last successful run happened, if any.</param>
public readonly record struct ReasonletMetrics(int Runs, int Successes, DateTimeOffset? LastSuccessAt)
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
