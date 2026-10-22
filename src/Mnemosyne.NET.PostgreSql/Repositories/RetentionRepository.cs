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
    /// Locks the candidates in id order and deletes them, then answers how many it deleted. Each delete method puts its
    /// candidate query in front: a subquery that names up to <c>@batchSize</c> ids, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executions are locked <c>FOR UPDATE</c> in id order, the order an erase locks the executions it removes, so
    /// a prune and an erase that meet on the same runs take them in one order. A candidate that a concurrent erase
    /// deleted while this statement waited for it is skipped rather than replaced, so a batch can come back short
    /// while older runs remain: only a batch that deletes nothing shows that the prune is done.
    /// </para>
    /// <para>
    /// The lock and the delete look the candidate ids up by primary key, as an array. Locking straight from a filter
    /// ordered by id left the plan to the estimates: a generic plan walked the whole primary key for a scope with no
    /// old runs, and the delete joined the batch against a sequential scan of the table.
    /// </para>
    /// <para>
    /// The metrics rows are left alone on purpose: a pruned run still happened, so the counters and timestamps keep
    /// it. That makes the counters the only complete record of the totals, which is why an erase subtracts what it
    /// removes rather than recounting what is left. Taking no metrics lock also means a prune never waits for an
    /// append, nor an append for a prune.
    /// </para>
    /// </remarks>
    private const string LockAndDeleteSql = """
            ORDER BY e.id
            FOR UPDATE OF e
        ), deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(SELECT d.id FROM doomed d))
            RETURNING e.id
        )
        SELECT count(*)::int FROM deleted
        """;

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> executions of every scope recorded before <paramref name="cutoff"/>,
    /// and answers how many it deleted. Their feedback cascades. <see cref="LockAndDeleteSql"/> gives the lock order.
    /// </summary>
    /// <remarks>
    /// A separate statement from <see cref="DeleteOldExecutionsInScopeAsync"/> because neither index serves both: one
    /// statement with an optional scope filter got a generic plan that walked the <c>executed_at</c> index for a
    /// scoped prune too, reading every other scope's old runs. Here the candidates walk the <c>executed_at</c> index
    /// from the oldest run and stop at the batch size, so a batch reads about what it deletes and never a recent run.
    /// The planner may still choose otherwise; this is the plan PostgreSQL 17 chose in testing.
    /// </remarks>
    [Query("""
        WITH doomed AS (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(
                SELECT c.id
                FROM mnemosyne.reasonlet_executions c
                WHERE c.executed_at < @cutoff
                ORDER BY c.executed_at
                LIMIT @batchSize))
        """ + LockAndDeleteSql)]
    public partial Task<int> DeleteOldExecutionsAsync(DateTime cutoff, int batchSize, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> executions of the scope recorded before <paramref name="cutoff"/>,
    /// and answers how many it deleted. Their feedback cascades. <see cref="LockAndDeleteSql"/> gives the lock order.
    /// </summary>
    /// <remarks>
    /// The candidates come from an index-only scan of <c>(scope, executed_at) INCLUDE (id)</c>, from the scope's oldest
    /// run, which stops at the batch size. So a batch reads about what it deletes and never another scope's runs: a
    /// tenant's prune costs the same however much the other tenants hold. The index carries the id for this. Without
    /// it, the planner preferred walking the <c>executed_at</c> index, whose order matches the heap's, and filtering out
    /// the other scopes, even in a custom plan for a scope with a tenth of the rows. The planner may still choose
    /// otherwise; this is the plan PostgreSQL 17 chose in testing, with custom and generic plans alike.
    /// </remarks>
    [Query("""
        WITH doomed AS (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(
                SELECT c.id
                FROM mnemosyne.reasonlet_executions c
                WHERE c.scope = @scope
                  AND c.executed_at < @cutoff
                ORDER BY c.executed_at
                LIMIT @batchSize))
        """ + LockAndDeleteSql)]
    public partial Task<int> DeleteOldExecutionsInScopeAsync(
        string scope, DateTime cutoff, int batchSize, IAsyncDbTransaction transaction, CancellationToken ct);
}
