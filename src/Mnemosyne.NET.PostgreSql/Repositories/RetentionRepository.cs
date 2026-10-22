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
    /// The statement picks the oldest <paramref name="batchSize"/> candidates, then locks them <c>FOR UPDATE</c> in id
    /// order, the order an erase locks the executions it removes, so a prune and an erase that meet on the same runs
    /// take them in one order. A candidate that a concurrent erase deleted while this statement waited for it is
    /// skipped rather than replaced, so a batch can come back short while older runs remain: only a batch that
    /// deletes nothing shows that the prune is done.
    /// </para>
    /// <para>
    /// The candidates are a separate subquery ordered by <c>executed_at</c>, so that the planner can walk the
    /// <c>executed_at</c> index from the oldest run and stop at the batch size, and the lock and the delete look the
    /// chosen ids up by primary key. Locking straight from a filter ordered by id left the plan to the estimates: a
    /// generic plan walked the whole primary key for a scope with no old runs, and the delete joined the batch against
    /// a sequential scan of the table. Without a scope a batch reads about what it deletes. With one, the
    /// <c>executed_at</c> walk also passes over other scopes' old runs, so in the worst case it reads every old run in
    /// the database, but never a recent one. The planner may still choose otherwise; these are the plans PostgreSQL 17
    /// chose in testing.
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
            WHERE e.id = ANY(ARRAY(
                SELECT c.id
                FROM mnemosyne.reasonlet_executions c
                WHERE c.executed_at < @cutoff
                  AND (@scope::text IS NULL OR c.scope = @scope)
                ORDER BY c.executed_at
                LIMIT @batchSize))
            ORDER BY e.id
            FOR UPDATE OF e
        ), deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(SELECT d.id FROM doomed d))
            RETURNING e.id
        )
        SELECT count(*)::int FROM deleted
        """)]
    public partial Task<int> DeleteOldExecutionsAsync(
        string? scope, DateTime cutoff, int batchSize, IAsyncDbTransaction transaction, CancellationToken ct);
}
