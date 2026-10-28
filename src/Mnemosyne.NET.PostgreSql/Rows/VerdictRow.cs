namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>An execution's verdict as the counters see it, read by position.</summary>
/// <param name="Success">The effective success.</param>
/// <param name="Judged">Whether an evaluator or a human gave the run a verdict, rather than only the agent.</param>
internal sealed record VerdictRow(bool Success, bool Judged)
{
    /// <summary>The run's share of <c>successes</c>: 1 when it succeeded, else 0.</summary>
    public int Successes => Success ? 1 : 0;

    /// <summary>The run's share of <c>judged_runs</c>: 1 when it is judged, else 0.</summary>
    public int JudgedRuns => Judged ? 1 : 0;

    /// <summary>The run's share of <c>judged_successes</c>: 1 when it is judged and succeeded, else 0.</summary>
    public int JudgedSuccesses => Judged && Success ? 1 : 0;
}
