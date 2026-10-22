using System.Data.Async;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Writes rows of <c>mnemosyne.reasonlet_executions</c> and <c>mnemosyne.reasonlet_feedback</c>.</summary>
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
}
