using System.Data.Async;
using Mnemosyne.NET.PostgreSql.Rows;
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
    /// <c>greatest</c>, which ignores nulls, keeps the latest success whatever order they commit in.
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
        INSERT INTO mnemosyne.reasonlet_metrics AS m (reasonlet_id, scope, runs, successes, last_success_at)
        SELECT reasonlet_id, scope, 1, CASE WHEN success THEN 1 ELSE 0 END, CASE WHEN success THEN executed_at END
        FROM e
        ON CONFLICT (reasonlet_id) DO UPDATE
            SET runs = m.runs + 1,
                successes = m.successes + EXCLUDED.successes,
                last_success_at = greatest(m.last_success_at, EXCLUDED.last_success_at)
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
    /// Deletes a scope's executions that an erasure removes: every execution of the Reasonlets it deletes, whoever ran
    /// them, and the subject's own executions of Reasonlets it keeps. Their feedback cascades. Answers how many it
    /// deleted, each once even when it is both, and the kept Reasonlets that lost executions, whose metrics need a
    /// recount.
    /// </summary>
    /// <remarks>
    /// The rows are locked in id order before any is deleted, so two erases that remove overlapping executions take
    /// them in one order and cannot deadlock. A Reasonlet of the subject that is kept can only have been remembered
    /// after the erasure took its locks, so its executions stay with it. Run it after
    /// <see cref="ReasonletRepository.LockForErasureAsync"/> and before the Reasonlets are deleted, so that no
    /// execution of a deleted Reasonlet is left to cascade uncounted.
    /// </remarks>
    [Query("""
        WITH doomed AS (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.scope = @scope
              AND (e.reasonlet_id = ANY(@reasonletIds)
                   OR (e.subject_id = @subjectId
                       AND NOT EXISTS (SELECT 1 FROM mnemosyne.reasonlets r
                                       WHERE r.id = e.reasonlet_id AND r.scope = @scope AND r.subject_id = @subjectId)))
            ORDER BY e.id
            FOR UPDATE OF e
        ), deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e USING doomed d
            WHERE e.id = d.id AND e.scope = @scope
            RETURNING e.reasonlet_id
        )
        SELECT count(*)::int,
               coalesce(array_agg(DISTINCT reasonlet_id) FILTER (WHERE reasonlet_id <> ALL(@reasonletIds)), '{}')
        FROM deleted
        """)]
    public partial Task<ErasedExecutionsRow?> DeleteForErasureAsync(
        string scope, Guid[] reasonletIds, string? subjectId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks the metrics rows of kept Reasonlets <c>FOR UPDATE</c>, in id order, until the transaction ends, so that
    /// no execution can be counted in them between the recount's snapshot and its write.
    /// </summary>
    /// <remarks>
    /// A separate statement from <see cref="RecountMetricsAsync"/> on purpose. An <c>UPDATE</c> that waits for a row
    /// lock re-checks only the row it waited for, with its first snapshot, so a recount that waited for a concurrent
    /// append would overwrite the append's count with a total that misses it. Once this statement holds the rows,
    /// an append waits for the erase instead, and the recount's fresh snapshot sees every execution committed before.
    /// </remarks>
    [Query("""
        SELECT count(*)::int FROM (
            SELECT 1 FROM mnemosyne.reasonlet_metrics
            WHERE scope = @scope AND reasonlet_id = ANY(@reasonletIds)
            ORDER BY reasonlet_id
            FOR UPDATE
        ) locked
        """)]
    public partial Task<int?> LockMetricsAsync(string scope, Guid[] reasonletIds, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Recounts the metrics rows of Reasonlets from their remaining executions. Run it after <see cref="LockMetricsAsync"/>.</summary>
    [Command("""
        UPDATE mnemosyne.reasonlet_metrics m
        SET runs = c.runs, successes = c.successes, last_success_at = c.last_success_at
        FROM (
            SELECT k.id,
                   count(e.id)::int AS runs,
                   (count(e.id) FILTER (WHERE e.success))::int AS successes,
                   max(e.executed_at) FILTER (WHERE e.success) AS last_success_at
            FROM unnest(@reasonletIds::uuid[]) k (id)
            LEFT JOIN mnemosyne.reasonlet_executions e ON e.scope = @scope AND e.reasonlet_id = k.id
            GROUP BY k.id
        ) c
        WHERE m.scope = @scope AND m.reasonlet_id = c.id
        """)]
    public partial Task<int> RecountMetricsAsync(string scope, Guid[] reasonletIds, IAsyncDbTransaction transaction, CancellationToken ct);
}
