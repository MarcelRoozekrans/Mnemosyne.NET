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
    /// <para>
    /// The Reasonlet is locked <c>FOR KEY SHARE</c> before the metrics row, so the statement takes its locks in the
    /// order an erase does: Reasonlet, then metrics. Left to the foreign-key check, the Reasonlet's lock would come at
    /// the end of the statement, after the metrics row, and an append could deadlock with an erase of its Reasonlet.
    /// The increment is one atomic <c>UPDATE</c> of the row, so concurrent executions never lose a count, and
    /// <c>greatest</c>, which ignores nulls, keeps the latest execution and the latest success whatever order they
    /// commit in.
    /// </para>
    /// <para>
    /// The run stores the agent's flag and the evaluator's verdict, when there is one, and its effective success,
    /// the evaluator's verdict before the agent's: a new run has no feedback yet. The metrics count the effective
    /// success. The table's CHECKs refuse an evaluation given only in part.
    /// </para>
    /// <para>
    /// A run with an evaluation is judged, so it also counts in <c>judged_runs</c>, and in <c>judged_successes</c>
    /// when its effective success is true. A run without one is agent-only and leaves both alone. The metrics row's
    /// CHECKs hold the judged counters within the run counters.
    /// </para>
    /// </remarks>
    private const string InsertSql = """
        WITH r AS (
            SELECT id, scope FROM mnemosyne.reasonlets
            WHERE id = @reasonletId AND scope = @scope
            FOR KEY SHARE
        ), e AS (
            INSERT INTO mnemosyne.reasonlet_executions
                (id, reasonlet_id, scope, executed_at, agent_success, evaluated_success, evaluation_score, evaluator, success,
                 duration_ticks, score, source, run_id, subject_id, tags)
            SELECT @id, r.id, r.scope, now(), @agentSuccess, @evaluatedSuccess, @evaluationScore, @evaluator,
                   coalesce(@evaluatedSuccess, @agentSuccess), @durationTicks, @score, @source, @runId, @subjectId, @tags
            FROM r
            RETURNING reasonlet_id, scope, success, evaluated_success IS NOT NULL AS judged, executed_at
        )
        INSERT INTO mnemosyne.reasonlet_metrics AS m
            (reasonlet_id, scope, runs, successes, judged_runs, judged_successes, last_success_at, last_executed_at)
        SELECT reasonlet_id, scope, 1, CASE WHEN success THEN 1 ELSE 0 END,
               CASE WHEN judged THEN 1 ELSE 0 END, CASE WHEN judged AND success THEN 1 ELSE 0 END,
               CASE WHEN success THEN executed_at END, executed_at
        FROM e
        ON CONFLICT (reasonlet_id) DO UPDATE
            SET runs = m.runs + 1,
                successes = m.successes + EXCLUDED.successes,
                judged_runs = m.judged_runs + EXCLUDED.judged_runs,
                judged_successes = m.judged_successes + EXCLUDED.judged_successes,
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
        bool agentSuccess,
        bool? evaluatedSuccess,
        double? evaluationScore,
        string? evaluator,
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
        bool agentSuccess,
        bool? evaluatedSuccess,
        double? evaluationScore,
        string? evaluator,
        long durationTicks,
        double? score,
        string source,
        string? runId,
        string? subjectId,
        [Json] string? tags,
        CancellationToken ct);

    /// <summary>
    /// Reads the Reasonlet of an execution in the scope, without locking anything: the first step of feedback, which
    /// knows only the execution. Answers null when the execution is not in the scope.
    /// </summary>
    [Query("SELECT reasonlet_id FROM mnemosyne.reasonlet_executions WHERE id = @executionId AND scope = @scope")]
    public partial Task<Guid?> GetReasonletOfAsync(Guid executionId, string scope, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks the Reasonlet in the scope <c>FOR KEY SHARE</c> until the transaction ends, the second step of feedback,
    /// and answers how many rows it locked: 0 when the Reasonlet is gone, deleted by an erase or a prune that this
    /// statement waited for.
    /// </summary>
    /// <remarks>
    /// The lock an append takes on its Reasonlet. It lets appends, remembers and other feedback through, and conflicts
    /// only with the <c>FOR UPDATE</c> of an erase or a prune batch, which delete the Reasonlet. Feedback holds
    /// nothing yet when it waits here.
    /// </remarks>
    [Query("""
        SELECT count(*)::int FROM (
            SELECT 1 FROM mnemosyne.reasonlets
            WHERE id = @reasonletId AND scope = @scope
            FOR KEY SHARE
        ) l
        """)]
    public partial Task<int> LockReasonletForFeedbackAsync(Guid reasonletId, string scope, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks the execution in the scope <c>FOR UPDATE</c> until the transaction ends, the third step of feedback, and
    /// answers its verdict before the feedback, its effective success and whether it is judged: null when it is gone,
    /// deleted by an erase or a prune that this statement waited for.
    /// </summary>
    /// <remarks>
    /// A statement of its own, so it re-checks the execution after any wait: under READ COMMITTED a locking read that
    /// waited for a delete finds no row. The verdict it answers cannot change until commit, because every writer of
    /// the row, feedback itself, locks it first. A run is judged when an evaluator or a human gave it a verdict. It
    /// also checks that the execution still belongs to the Reasonlet the feedback locked, rather than assuming
    /// <c>reasonlet_id</c> never changes, so the locks it holds are always the run's own Reasonlet and metrics row.
    /// </remarks>
    [Query("""
        SELECT success, evaluated_success IS NOT NULL OR feedback_worked IS NOT NULL
        FROM mnemosyne.reasonlet_executions
        WHERE id = @executionId AND scope = @scope AND reasonlet_id = @reasonletId
        FOR UPDATE
        """)]
    public partial Task<VerdictRow?> LockExecutionForFeedbackAsync(
        Guid executionId, string scope, Guid reasonletId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks the Reasonlet's metrics row <c>FOR UPDATE</c> until the transaction ends, the fourth step of feedback.
    /// The rows are folded into a count that the command discards.
    /// </summary>
    /// <remarks>
    /// A statement of its own, before the counters are adjusted, so the statement that recomputes
    /// <c>last_success_at</c> takes its snapshot with the row held and sees every append that committed before.
    /// </remarks>
    [Command("""
        SELECT count(*)::int FROM (
            SELECT 1 FROM mnemosyne.reasonlet_metrics
            WHERE reasonlet_id = @reasonletId
            FOR UPDATE
        ) l
        """)]
    public partial Task LockMetricsForFeedbackAsync(Guid reasonletId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Records feedback on an execution that this transaction holds, replacing earlier feedback, and applies its
    /// verdict, in one statement. Answers the execution's verdict afterwards: its effective success, and whether it is
    /// judged, which a non-null <paramref name="worked"/> makes it.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="worked"/> keeps the stored human verdict, so a verdict can be changed but not retracted.
    /// The effective success is recomputed from the stored inputs, the human's verdict before the evaluator's before
    /// the agent's, which the table's CHECK also enforces. The new feedback row's foreign-key check locks the
    /// execution <c>FOR KEY SHARE</c>, which this transaction already holds <c>FOR UPDATE</c>.
    /// </remarks>
    [Query("""
        WITH feedback AS (
            INSERT INTO mnemosyne.reasonlet_feedback
                (execution_id, scope, utility, correctness, reusability, recorded_at)
            VALUES (@executionId, @scope, @utility, @correctness, @reusability, now())
            ON CONFLICT (execution_id) DO UPDATE
                SET utility = EXCLUDED.utility,
                    correctness = EXCLUDED.correctness,
                    reusability = EXCLUDED.reusability,
                    recorded_at = EXCLUDED.recorded_at
        ), verdict AS (
            UPDATE mnemosyne.reasonlet_executions
            SET feedback_worked = coalesce(@worked, feedback_worked),
                success = coalesce(@worked, feedback_worked, evaluated_success, agent_success)
            WHERE id = @executionId AND scope = @scope
            RETURNING success, evaluated_success IS NOT NULL OR feedback_worked IS NOT NULL AS judged
        )
        SELECT success, judged FROM verdict
        """)]
    public partial Task<VerdictRow?> RecordFeedbackAsync(
        Guid executionId,
        string scope,
        double utility,
        double correctness,
        double reusability,
        bool? worked,
        IAsyncDbTransaction transaction,
        CancellationToken ct);

    /// <summary>
    /// Moves the Reasonlet's counters by the change feedback made to one run's verdict: <paramref name="successes"/>,
    /// <paramref name="judgedRuns"/> and <paramref name="judgedSuccesses"/> are each -1, 0 or 1. A success raised from
    /// a failure moves <c>last_success_at</c> to the run's time when that is later; <c>greatest</c> ignores a null. A
    /// success lost, when the run's time is at or after <c>last_success_at</c>, so that the lost success held it,
    /// recomputes <c>last_success_at</c> as the latest remaining effective success of the Reasonlet, or null when none
    /// remains. No change to <c>successes</c> leaves it alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run it after <see cref="RecordFeedbackAsync"/>, in a later statement, so a lowered run reads as a failure here
    /// and is not its own latest success. Retention leaves pruned runs in the counters, so <c>successes</c> can stay
    /// above zero while <c>last_success_at</c> becomes null: the time of a pruned success is no longer known. That is
    /// what an erase does too.
    /// </para>
    /// <para>
    /// The judged counters move with the run's judged state, not only with its success: a verdict that agrees with the
    /// agent's makes an agent-only run judged without changing whether it succeeded. Feedback cannot retract a
    /// verdict, so <c>judged_runs</c> never falls here. One statement moves all three, so the metrics row's CHECKs see
    /// them in step.
    /// </para>
    /// </remarks>
    [Command("""
        UPDATE mnemosyne.reasonlet_metrics m
        SET successes = m.successes + @successes,
            judged_runs = m.judged_runs + @judgedRuns,
            judged_successes = m.judged_successes + @judgedSuccesses,
            last_success_at = CASE
                WHEN @successes > 0
                THEN greatest(m.last_success_at,
                    (SELECT e.executed_at FROM mnemosyne.reasonlet_executions e WHERE e.id = @executionId))
                WHEN @successes < 0
                     AND (SELECT e.executed_at FROM mnemosyne.reasonlet_executions e WHERE e.id = @executionId) >= m.last_success_at
                THEN (SELECT max(e.executed_at) FROM mnemosyne.reasonlet_executions e
                      WHERE e.scope = m.scope AND e.reasonlet_id = m.reasonlet_id AND e.success)
                ELSE m.last_success_at END
        WHERE m.reasonlet_id = @reasonletId
        """)]
    public partial Task MoveCountersAsync(
        Guid executionId, Guid reasonletId, int successes, int judgedRuns, int judgedSuccesses, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in id order, every execution of <paramref name="doomedIds"/> in the scope, whoever ran
    /// it, then deletes them, in one statement, and answers how many it deleted. Their feedback cascades. The
    /// executions step of a scope erase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second of a scope erase's lock steps, after <see cref="ReasonletRepository.LockForErasureAsync"/>, which
    /// answers <paramref name="doomedIds"/>: Reasonlets, then executions, then the metrics rows, which the final delete
    /// of the Reasonlets cascades to. A scope erase keeps no Reasonlet, so it subtracts from no metrics row, and the
    /// executions can be found by their Reasonlet rather than by an id list read back to the client. The Reasonlet
    /// locks hold every new execution of them off until commit, so the delete finds exactly the rows it locked.
    /// </para>
    /// <para>
    /// The locks are taken in the CTE, which an <c>ORDER BY</c> sorts before <c>FOR UPDATE</c> locks each row. The
    /// delete finds the same rows through the <c>(scope, reasonlet_id)</c> index and keeps only those the CTE locked,
    /// by a semi-join rather than an array, so no value has to hold every execution of a scope. A row the delete
    /// reaches is one the CTE has already locked, whichever side of the join the planner runs first, so the
    /// statement takes no execution lock out of id order. PostgreSQL 17 hashed the CTE for a large scope and probed it
    /// in a nested loop for a small one, in testing. An execution that another erase deleted while this statement
    /// waited for it is skipped.
    /// </para>
    /// </remarks>
    [Query("""
        WITH locked AS MATERIALIZED (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.scope = @scope AND e.reasonlet_id = ANY(@doomedIds)
            ORDER BY e.id
            FOR UPDATE OF e
        ), deleted AS (
            DELETE FROM mnemosyne.reasonlet_executions e
            WHERE e.scope = @scope AND e.reasonlet_id = ANY(@doomedIds)
              AND e.id IN (SELECT l.id FROM locked l)
            RETURNING e.id
        )
        SELECT count(*)::int FROM deleted
        """)]
    public partial Task<int> DeleteScopeExecutionsAsync(string scope, Guid[] doomedIds, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Creates the temporary table <c>pg_temp.mnemosyne_erased_executions</c>, which holds the ids of the executions a
    /// subject erase locked, with their Reasonlets, until the transaction ends. The first step of a subject erase
    /// after <see cref="ReasonletRepository.LockForErasureAsync"/>.
    /// </summary>
    /// <remarks>
    /// The erase must delete exactly the executions it locked, not rows written since, and its later statements take
    /// fresh snapshots. The table keeps that set on the server, so no id list travels to the client and back, and the
    /// later statements join to it, so no array value has to hold every execution of a subject. <c>ON COMMIT DROP</c> drops it with the transaction, and a
    /// rollback drops it too, so a pooled connection never keeps it. It is named in <c>pg_temp</c>, so no
    /// <c>search_path</c> can make a statement read another table of that name.
    /// </remarks>
    [Command("CREATE TEMPORARY TABLE pg_temp.mnemosyne_erased_executions (id uuid NOT NULL, reasonlet_id uuid NOT NULL) ON COMMIT DROP")]
    public partial Task CreateErasedExecutionsAsync(IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in id order and until the transaction ends, a scope's executions that a subject erase
    /// removes, and records them in <c>pg_temp.mnemosyne_erased_executions</c>: every execution of
    /// <paramref name="doomedIds"/>, whoever ran it, and the subject's own executions of the Reasonlets the erase
    /// keeps. Answers how many it recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second of a subject erase's three lock steps: <see cref="ReasonletRepository.LockForErasureAsync"/>, which
    /// answers <paramref name="doomedIds"/>, then this, then <see cref="LockMetricsForErasureAsync"/>. Every erase
    /// takes its locks in that order: Reasonlets, then executions, then metrics rows. The final delete of the
    /// Reasonlets runs after <see cref="DeleteForErasureAsync"/> has removed their executions, so its cascade touches
    /// only the doomed Reasonlets' metrics rows. So two erases that overlap meet first on executions, where one waits
    /// for the other before it holds any metrics row, and they cannot deadlock. Locking a kept Reasonlet's metrics
    /// row before its executions would let each erase hold a metrics row that the other's cascade needs, while it
    /// waits for the other's executions.
    /// </para>
    /// <para>
    /// The <c>ORDER BY</c> sorts the rows before <c>FOR UPDATE</c> locks each one, so the insert records them in the
    /// order it locked them. A Reasonlet of the subject that is not in <paramref name="doomedIds"/> was remembered after
    /// the erasure took its Reasonlet locks. Its executions are left out, so it survives with them, as the erasure
    /// covers only what existed when it took its locks. An execution that another erase deleted while this statement
    /// waited for it is skipped.
    /// </para>
    /// </remarks>
    [Command("""
        INSERT INTO pg_temp.mnemosyne_erased_executions (id, reasonlet_id)
        SELECT e.id, e.reasonlet_id
        FROM mnemosyne.reasonlet_executions e
        WHERE e.scope = @scope
          AND (e.reasonlet_id = ANY(@doomedIds)
               OR (e.subject_id = @subjectId
                   AND NOT EXISTS (SELECT 1 FROM mnemosyne.reasonlets r
                                   WHERE r.id = e.reasonlet_id AND r.scope = @scope AND r.subject_id = @subjectId)))
        ORDER BY e.id
        FOR UPDATE OF e
        """)]
    public partial Task<int> LockExecutionsForErasureAsync(
        string scope, Guid[] doomedIds, string subjectId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in <c>reasonlet_id</c> order and until the transaction ends, the metrics rows of the
    /// Reasonlets outside <paramref name="doomedIds"/> that the executions in <c>pg_temp.mnemosyne_erased_executions</c>
    /// belong to, and answers their ids. Those are the kept Reasonlets whose counters
    /// <see cref="DeleteForErasureAsync"/> subtracts from.
    /// </summary>
    /// <remarks>
    /// The third of a subject erase's lock steps, after <see cref="LockExecutionsForErasureAsync"/>, which gives the
    /// lock order and why it is the cascade's. A separate statement from the delete on purpose: the delete's snapshot
    /// is taken once these rows are held, so it sees every append to these Reasonlets that committed before, and an
    /// append that comes later waits for the erase. An <c>UPDATE</c> that waited for the rows itself would re-check
    /// only the row it waited for, with its first snapshot, and would recompute the timestamps from executions that
    /// miss the append it waited for. The metrics rows are joined to the temporary table rather than read from an
    /// array of its rows. The table has no statistics, but the planner sizes it from its pages, and in testing
    /// PostgreSQL 17 probed the metrics primary key once per Reasonlet on a large table, and hashed a small one.
    /// </remarks>
    [Query("""
        SELECT coalesce(array_agg(l.reasonlet_id), '{}') FROM (
            SELECT m.reasonlet_id
            FROM mnemosyne.reasonlet_metrics m
            WHERE m.reasonlet_id IN (SELECT t.reasonlet_id FROM pg_temp.mnemosyne_erased_executions t)
              AND m.scope = @scope
              AND m.reasonlet_id <> ALL(@doomedIds)
            ORDER BY m.reasonlet_id
            FOR UPDATE OF m
        ) l
        """)]
    public partial Task<Guid[]> LockMetricsForErasureAsync(
        string scope, Guid[] doomedIds, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Deletes the executions that <see cref="LockExecutionsForErasureAsync"/> locked and recorded in
    /// <c>pg_temp.mnemosyne_erased_executions</c>, and subtracts them from the metrics rows of
    /// <paramref name="keptIds"/>, in one statement. Their feedback cascades. Answers how many it deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run it after <see cref="LockMetricsForErasureAsync"/>, which answers <paramref name="keptIds"/>, and before the
    /// Reasonlets are deleted, so that no execution of a deleted Reasonlet is left to cascade uncounted. It deletes
    /// exactly the locked ids, so it takes no execution lock while it holds metrics rows, which keeps the erase's lock
    /// order. A subject's execution appended to a kept Reasonlet after the locks is not among them, and is neither
    /// deleted nor subtracted. It joins to the table rather than reading an array of its ids; in testing PostgreSQL 17
    /// probed the executions primary key once per row on a large table.
    /// </para>
    /// <para>
    /// The counters are subtracted, not recounted: retention prunes old executions and leaves their counts, so the
    /// executions left are not the whole history. The judged counters lose the removed runs that an evaluator or a
    /// human judged, and those of them whose effective success is true, so a removed agent-only run leaves them alone.
    /// A doomed Reasonlet's metrics row is not updated, because it cascades with the Reasonlet. A timestamp is recomputed
    /// only when the latest removed value is at or after the stored one, as the latest remaining execution, or success,
    /// of the Reasonlet. Every CTE of a statement reads one snapshot, in which the deleted rows are still visible, so
    /// "remaining" leaves out the deleted ids explicitly.
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
            USING pg_temp.mnemosyne_erased_executions t
            WHERE e.scope = @scope AND e.id = t.id
            RETURNING e.id, e.reasonlet_id, e.success, e.evaluated_success IS NOT NULL OR e.feedback_worked IS NOT NULL AS judged,
                      e.executed_at
        ), removed AS (
            SELECT reasonlet_id,
                   count(*)::int AS runs,
                   (count(*) FILTER (WHERE success))::int AS successes,
                   (count(*) FILTER (WHERE judged))::int AS judged_runs,
                   (count(*) FILTER (WHERE judged AND success))::int AS judged_successes,
                   max(executed_at) AS last_executed_at,
                   max(executed_at) FILTER (WHERE success) AS last_success_at
            FROM deleted
            WHERE reasonlet_id = ANY(@keptIds)
            GROUP BY reasonlet_id
        ), subtracted AS (
            UPDATE mnemosyne.reasonlet_metrics m
            SET runs = m.runs - x.runs,
                successes = m.successes - x.successes,
                judged_runs = m.judged_runs - x.judged_runs,
                judged_successes = m.judged_successes - x.judged_successes,
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
    public partial Task<int> DeleteForErasureAsync(string scope, Guid[] keptIds, IAsyncDbTransaction transaction, CancellationToken ct);
}
