using System.Data.Async;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Writes and deletes rows of <c>mnemosyne.reasonlet_executions</c> and <c>mnemosyne.reasonlet_feedback</c>.</summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class ExecutionRepository(IAsyncDbConnection connection)
{
    [Command("""
        INSERT INTO mnemosyne.reasonlet_executions
            (id, reasonlet_id, scope, executed_at, success, duration_ticks, score, source, run_id, subject_id, tags)
        VALUES
            (@id, @reasonletId, @scope, now(), @success, @durationTicks, @score, @source, @runId, @subjectId, @tags)
        """)]
    public partial Task InsertAsync(
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

    /// <summary>Inserts an execution of a Reasonlet that exists in the scope, and answers the rows inserted: 0 when it does not.</summary>
    [Command("""
        INSERT INTO mnemosyne.reasonlet_executions
            (id, reasonlet_id, scope, executed_at, success, duration_ticks, score, source, run_id, subject_id, tags)
        SELECT @id, r.id, r.scope, now(), @success, @durationTicks, @score, @source, @runId, @subjectId, @tags
        FROM mnemosyne.reasonlets r
        WHERE r.id = @reasonletId AND r.scope = @scope
        """)]
    public partial Task<int> InsertForExistingAsync(
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
    /// Deletes, and answers the number of, a scope's executions that an erasure removes: every execution of the
    /// Reasonlets it deletes, whoever ran them, and the subject's own executions of Reasonlets it keeps. Their
    /// feedback cascades. Each execution is deleted and counted once, even when it is both.
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
                                       WHERE r.id = e.reasonlet_id AND r.subject_id = @subjectId)))
            ORDER BY e.id
            FOR UPDATE OF e
        ), deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e USING doomed d
            WHERE e.id = d.id
            RETURNING 1
        )
        SELECT count(*)::int FROM deleted
        """)]
    public partial Task<int> DeleteForErasureAsync(
        string scope, Guid[] reasonletIds, string? subjectId, IAsyncDbTransaction transaction, CancellationToken ct);
}
