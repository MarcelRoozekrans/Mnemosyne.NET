using System.Data.Async;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>
/// Writes and deletes rows of <c>mnemosyne.reasonlet_executions</c> and <c>mnemosyne.reasonlet_feedback</c>, and keeps
/// <c>mnemosyne.reasonlet_metrics</c> in step with the executions.
/// </summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class ExecutionRepository(IAsyncDbConnection connection)
{
    /// <summary>
    /// Inserts an execution of a Reasonlet in the scope and counts it in the Reasonlet's metrics row, in one statement.
    /// Answers the rows written: 0 when the Reasonlet is not in the scope.
    /// </summary>
    /// <remarks>
    /// The Reasonlet is locked <c>FOR KEY SHARE</c> before the metrics row, so the statement takes its locks in the
    /// order an erase does: Reasonlet, then metrics. Left to the foreign-key check, the Reasonlet's lock would come at
    /// the end of the statement, after the metrics row, and an append could deadlock with an erase of its Reasonlet.
    /// The increment is one atomic <c>UPDATE</c> of the row, so concurrent executions never lose a count, and
    /// <c>greatest</c>, which ignores nulls, keeps the latest execution and the latest success whatever order they
    /// commit in.
    /// </remarks>
    private const string InsertSql = """
        WITH r AS (
            SELECT id, scope FROM mnemosyne.reasonlets
            WHERE id = @reasonletId AND scope = @scope
            FOR KEY SHARE
        ), e AS (
            INSERT INTO mnemosyne.reasonlet_executions
                (id, reasonlet_id, scope, executed_at, success, duration_ticks, score, source, run_id, subject_id, tags)
            SELECT @id, r.id, r.scope, now(), @success, @durationTicks, @score, @source, @runId, @subjectId, @tags
            FROM r
            RETURNING reasonlet_id, scope, success, executed_at
        )
        INSERT INTO mnemosyne.reasonlet_metrics AS m (reasonlet_id, scope, runs, successes, last_success_at, last_executed_at)
        SELECT reasonlet_id, scope, 1, CASE WHEN success THEN 1 ELSE 0 END, CASE WHEN success THEN executed_at END, executed_at
        FROM e
        ON CONFLICT (reasonlet_id) DO UPDATE
            SET runs = m.runs + 1,
                successes = m.successes + EXCLUDED.successes,
                last_success_at = greatest(m.last_success_at, EXCLUDED.last_success_at),
                last_executed_at = greatest(m.last_executed_at, EXCLUDED.last_executed_at)
        """;

    /// <summary>
    /// Inserts an execution of a Reasonlet in the scope and counts it in the Reasonlet's metrics row, in one statement
    /// inside the caller's transaction, which already holds or has just inserted the Reasonlet. Answers the rows written.
    /// </summary>
    [Command(InsertSql)]
    public partial Task<int> InsertAsync(
        Guid id,
        Guid reasonletId,
        string scope,
        bool success,
        long durationTicks,
        double? score,
        string source,
        string? runId,
        string? subjectId,
        [Json] string? tags,
        IAsyncDbTransaction transaction,
        CancellationToken ct);

    /// <summary>
    /// Inserts an execution of a Reasonlet in the scope and counts it in the Reasonlet's metrics row, in one statement.
    /// Answers the rows written: 0 when the Reasonlet is not in the scope, which includes one a concurrent erase
    /// deleted while this statement waited for its lock.
    /// </summary>
    [Command(InsertSql)]
    public partial Task<int> InsertAsync(
        Guid id,
        Guid reasonletId,
        string scope,
        bool success,
        long durationTicks,
        double? score,
        string source,
        string? runId,
        string? subjectId,
        [Json] string? tags,
        CancellationToken ct);

    /// <summary>Records feedback on an execution in the scope, replacing earlier feedback, and answers the rows written: 0 when it is not in the scope.</summary>
    [Command("""
        INSERT INTO mnemosyne.reasonlet_feedback
            (execution_id, scope, utility, correctness, reusability, recorded_at)
        SELECT e.id, e.scope, @utility, @correctness, @reusability, now()
        FROM mnemosyne.reasonlet_executions e
        WHERE e.id = @executionId AND e.scope = @scope
        ON CONFLICT (execution_id) DO UPDATE
            SET utility = EXCLUDED.utility,
                correctness = EXCLUDED.correctness,
                reusability = EXCLUDED.reusability,
                recorded_at = EXCLUDED.recorded_at
        """)]
    public partial Task<int> UpsertFeedbackAsync(
        Guid executionId,
        string scope,
        double utility,
        double correctness,
        double reusability,
        CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in id order and until the transaction ends, a scope's executions that an erasure
    /// removes, and answers their ids: every execution of <paramref name="doomedIds"/>, whoever ran it, and the
    /// subject's own executions of the Reasonlets the erasure keeps. A scope erase passes a null
    /// <paramref name="subjectId"/>, so only the doomed Reasonlets' executions are locked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second of the erase's three lock steps: <see cref="ReasonletRepository.LockForErasureAsync"/>, which answers
    /// <paramref name="doomedIds"/>, then this, then <see cref="LockMetricsForErasureAsync"/>. Every erase takes its
    /// locks in that order: Reasonlets, then executions, then metrics rows. The final delete of the Reasonlets runs
    /// after <see cref="DeleteForErasureAsync"/> has removed their executions, so its cascade touches only the doomed
    /// Reasonlets' metrics rows. So two erases that overlap meet first on executions, where one waits for the other
    /// before it holds any metrics row, and they cannot deadlock. Locking a kept Reasonlet's metrics row before its
    /// executions would let each erase hold a metrics row that the other's cascade needs, while it waits for the
    /// other's executions.
    /// </para>
    /// <para>
    /// A Reasonlet of the subject that is not in <paramref name="doomedIds"/> was remembered after the erasure took its
    /// Reasonlet locks. Its executions are left out, so it survives with them, as the erasure covers only what existed
    /// when it took its locks. An execution that another erase deleted while this statement waited for it is skipped.
    /// </para>
    /// </remarks>
    [Query("""
        SELECT coalesce(array_agg(l.id), '{}') FROM (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.scope = @scope
              AND (e.reasonlet_id = ANY(@doomedIds)
                   OR (e.subject_id = @subjectId
                       AND NOT EXISTS (SELECT 1 FROM mnemosyne.reasonlets r
                                       WHERE r.id = e.reasonlet_id AND r.scope = @scope AND r.subject_id = @subjectId)))
            ORDER BY e.id
            FOR UPDATE OF e
        ) l
        """)]
    public partial Task<Guid[]> LockExecutionsForErasureAsync(
        string scope, Guid[] doomedIds, string? subjectId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in <c>reasonlet_id</c> order and until the transaction ends, the metrics rows of the
    /// Reasonlets outside <paramref name="doomedIds"/> that <paramref name="executionIds"/> belong to, and answers
    /// their ids. Those are the kept Reasonlets whose counters <see cref="DeleteForErasureAsync"/> subtracts from; a
    /// scope erase keeps none.
    /// </summary>
    /// <remarks>
    /// The third of the erase's lock steps, after <see cref="LockExecutionsForErasureAsync"/>, which answers
    /// <paramref name="executionIds"/>; that method gives the lock order and why it is the cascade's. A separate
    /// statement from the delete on purpose: the delete's snapshot is taken once these rows are held, so it sees every
    /// append to these Reasonlets that committed before, and an append that comes later waits for the erase. An
    /// <c>UPDATE</c> that waited for the rows itself would re-check only the row it waited for, with its first
    /// snapshot, and would recompute the timestamps from executions that miss the append it waited for.
    /// </remarks>
    [Query("""
        SELECT coalesce(array_agg(l.reasonlet_id), '{}') FROM (
            SELECT m.reasonlet_id
            FROM mnemosyne.reasonlet_metrics m
            WHERE m.scope = @scope
              AND m.reasonlet_id <> ALL(@doomedIds)
              AND m.reasonlet_id IN (SELECT e.reasonlet_id FROM mnemosyne.reasonlet_executions e
                                     WHERE e.scope = @scope AND e.id = ANY(@executionIds))
            ORDER BY m.reasonlet_id
            FOR UPDATE OF m
        ) l
        """)]
    public partial Task<Guid[]> LockMetricsForErasureAsync(
        string scope, Guid[] executionIds, Guid[] doomedIds, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Deletes the executions that <see cref="LockExecutionsForErasureAsync"/> locked, and subtracts them from the
    /// metrics rows of <paramref name="keptIds"/>, in one statement. Their feedback cascades. Answers how many it
    /// deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run it after <see cref="LockMetricsForErasureAsync"/>, which answers <paramref name="keptIds"/>, and before the
    /// Reasonlets are deleted, so that no execution of a deleted Reasonlet is left to cascade uncounted. It deletes
    /// exactly the locked ids, so it takes no execution lock while it holds metrics rows, which keeps the erase's lock
    /// order. A subject's execution appended to a kept Reasonlet after the locks is not among them, and is neither
    /// deleted nor subtracted.
    /// </para>
    /// <para>
    /// The counters are subtracted, not recounted: retention prunes old executions and leaves their counts, so the
    /// executions left are not the whole history. A doomed Reasonlet's metrics row is not updated, because it cascades
    /// with the Reasonlet. A timestamp is recomputed only when the latest removed value is at or after the stored one,
    /// as the latest remaining execution, or success, of the Reasonlet. Every CTE of a statement reads one snapshot, in
    /// which the deleted rows are still visible, so "remaining" leaves out the deleted ids explicitly.
    /// </para>
    /// <para>
    /// When no qualifying execution remains, the stored value cannot stay: the latest execution is always the one that
    /// set it, so it is the erased subject's own run time. <c>last_executed_at</c> falls back to the Reasonlet's
    /// <c>created_at</c> instead, a true lower bound with no link to the subject, which keeps the column non-null: a
    /// null would read as never used to retention. <c>last_success_at</c> falls back to null, meaning unknown, even
    /// when <c>successes</c> still counts successes whose records were pruned.
    /// </para>
    /// </remarks>
    [Query("""
        WITH deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e
            WHERE e.scope = @scope AND e.id = ANY(@executionIds)
            RETURNING e.id, e.reasonlet_id, e.success, e.executed_at
        ), removed AS (
            SELECT reasonlet_id,
                   count(*)::int AS runs,
                   (count(*) FILTER (WHERE success))::int AS successes,
                   max(executed_at) AS last_executed_at,
                   max(executed_at) FILTER (WHERE success) AS last_success_at
            FROM deleted
            WHERE reasonlet_id = ANY(@keptIds)
            GROUP BY reasonlet_id
        ), subtracted AS (
            UPDATE mnemosyne.reasonlet_metrics m
            SET runs = m.runs - x.runs,
                successes = m.successes - x.successes,
                last_executed_at = CASE WHEN x.last_executed_at >= m.last_executed_at
                    THEN coalesce((SELECT max(e.executed_at) FROM mnemosyne.reasonlet_executions e
                                   WHERE e.scope = @scope AND e.reasonlet_id = m.reasonlet_id
                                     AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.id = e.id)),
                                  (SELECT r.created_at FROM mnemosyne.reasonlets r
                                   WHERE r.scope = @scope AND r.id = m.reasonlet_id))
                    ELSE m.last_executed_at END,
                last_success_at = CASE WHEN x.last_success_at >= m.last_success_at
                    THEN (SELECT max(e.executed_at) FROM mnemosyne.reasonlet_executions e
                          WHERE e.scope = @scope AND e.reasonlet_id = m.reasonlet_id AND e.success
                            AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.id = e.id))
                    ELSE m.last_success_at END
            FROM removed x
            WHERE m.scope = @scope AND m.reasonlet_id = x.reasonlet_id
        )
        SELECT count(*)::int FROM deleted
        """)]
    public partial Task<int?> DeleteForErasureAsync(
        string scope, Guid[] executionIds, Guid[] keptIds, IAsyncDbTransaction transaction, CancellationToken ct);
}
