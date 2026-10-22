using System.Data.Async;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Deletes what a retention policy no longer keeps, one bounded batch per call.</summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class RetentionRepository(IAsyncDbConnection connection)
{
    /// <summary>
    /// Reads the database's clock. A prune reads it once, before its first batch, and measures every window back from
    /// it, so a long prune has a fixed target and ends: rows that age past the cutoff while it runs wait for the next.
    /// </summary>
    [Query("SELECT now()")]
    public partial Task<DateTime?> NowAsync(CancellationToken ct);

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> executions recorded before <paramref name="cutoff"/>, in the scope or
    /// in every scope when <paramref name="scope"/> is null, and answers how many it deleted. Their feedback cascades.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executions are locked <c>FOR UPDATE</c> in id order before the delete, the order an erase locks the
    /// executions it removes, so a prune and an erase that meet on the same runs take them in one order. Execution ids
    /// are version 7 UUIDs, which grow with time, so the primary-key walk in id order reaches the old runs first and a
    /// batch reads little more than it deletes. A run that a concurrent erase deleted while this statement waited for
    /// it is skipped.
    /// </para>
    /// <para>
    /// The metrics rows are left alone on purpose: a pruned run still happened, so the counters and timestamps keep
    /// it. That makes the counters the only complete record of the totals, which is why an erase subtracts what it
    /// removes rather than recounting what is left. Taking no metrics lock also means a prune never waits for an
    /// append, nor an append for a prune.
    /// </para>
    /// </remarks>
    [Query("""
        WITH doomed AS (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.executed_at < @cutoff
              AND (@scope::text IS NULL OR e.scope = @scope)
            ORDER BY e.id
            LIMIT @batchSize
            FOR UPDATE OF e
        ), deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e
            USING doomed d
            WHERE e.id = d.id
            RETURNING e.id
        )
        SELECT count(*)::int FROM deleted
        """)]
    public partial Task<int> DeleteOldExecutionsAsync(
        string? scope, DateTime cutoff, int batchSize, IAsyncDbTransaction transaction, CancellationToken ct);
}
