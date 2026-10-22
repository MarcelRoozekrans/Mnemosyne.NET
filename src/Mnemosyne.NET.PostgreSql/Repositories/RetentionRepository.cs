using System.Data.Async;
using Mnemosyne.NET.PostgreSql.Rows;
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

    /// <summary>
    /// The start of the statements that list idle Reasonlets. <see cref="ListIdleReasonletsAsync"/> and
    /// <see cref="ListIdleReasonletsInScopeAsync"/> each follow it with the rows the prune covers, with their metrics,
    /// which closes the CTE <c>candidate</c>.
    /// </summary>
    private const string IdleListHead = """
        WITH RECURSIVE candidate AS MATERIALIZED (
            SELECT r.id, r.parent_id, r.scope, r.created_at, m.last_executed_at

        """;

    /// <summary>
    /// The rest of the statements that list idle Reasonlets, after the <c>candidate</c> rows. It answers their ids
    /// deepest first, by the depth below the top of the lineage, then by id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Reasonlet is used when it was remembered at or after the cutoff, or last ran at or after it. Use is judged
    /// per lineage, so the used closure is the used rows and every ancestor of one: a walk up <c>parent_id</c>. Every
    /// candidate outside the closure is idle. It follows that every child of an idle row is idle, so taking the list
    /// in chunks, deepest first, means that when a batch holds a row at depth k, every listed row deeper than k, its
    /// children among them, is in the same chunk or went in an earlier one, so a whole idle subtree goes in one prune.
    /// The order serves only that. It is not what keeps a parent from going without its children, which is the
    /// re-check's rule that a descendant the batch does not hold keeps the row; and a batch locks its chunk without
    /// waiting, so the order it locks in cannot make it deadlock.
    /// </para>
    /// <para>
    /// The depth is computed as <see cref="ReasonletRepository.LockForErasureAsync"/> computes it: a walk up from the
    /// idle rows finds the tops of their lineages, and a walk down from the tops numbers every row below them. The
    /// scope is carried along each walk rather than bound, so the same walks serve a prune of every scope. Each step
    /// of each walk is an index probe in a <c>LATERAL</c> subquery that <c>LIMIT 1</c> or <c>OFFSET 0</c> keeps from
    /// being flattened, and the walks up use <c>UNION</c>, so each visits a row once and the whole statement is linear
    /// in the rows it covers. A row on a hand-written <c>parent_id</c> cycle, which no walk down reaches, sorts at
    /// depth 0, as in an erase.
    /// </para>
    /// <para>
    /// The list is one statement that answers one array, so it can hold no more ids than one PostgreSQL array value
    /// can, about 1 GB: some 60 million Reasonlets idle at once, at 16 bytes an id. A prune past that fails; a store
    /// that size needs a prune limited to a scope, or run more often.
    /// </para>
    /// </remarks>
    private const string IdleListTail = """
        ), used (id, parent_id, scope) AS (
            SELECT c.id, c.parent_id, c.scope
            FROM candidate c
            WHERE c.created_at >= @cutoff OR c.last_executed_at >= @cutoff
            UNION
            SELECT p.id, p.parent_id, p.scope
            FROM used CROSS JOIN LATERAL (
                SELECT r.id, r.parent_id, r.scope FROM mnemosyne.reasonlets r
                WHERE r.id = used.parent_id AND r.scope = used.scope
                LIMIT 1
            ) p
        ), idle AS (
            SELECT c.id, c.parent_id, c.scope FROM candidate c
            WHERE NOT EXISTS (SELECT 1 FROM used u WHERE u.id = c.id)
        ), up (id, parent_id, scope) AS (
            SELECT id, parent_id, scope FROM idle
            UNION
            SELECT p.id, p.parent_id, p.scope
            FROM up CROSS JOIN LATERAL (
                SELECT r.id, r.parent_id, r.scope FROM mnemosyne.reasonlets r
                WHERE r.id = up.parent_id AND r.scope = up.scope
                LIMIT 1
            ) p
        ), down (id, scope, depth) AS (
            SELECT id, scope, 0 FROM up WHERE parent_id IS NULL
            UNION ALL
            SELECT c.id, c.scope, down.depth + 1
            FROM down CROSS JOIN LATERAL (
                SELECT r.id, r.scope FROM mnemosyne.reasonlets r
                WHERE r.scope = down.scope AND r.parent_id = down.id
                OFFSET 0
            ) c
        )
        SELECT coalesce(array_agg(i.id ORDER BY coalesce(d.depth, 0) DESC, i.id), '{}')
        FROM idle i LEFT JOIN down d ON d.id = i.id
        """;

    /// <summary>
    /// Lists every idle Reasonlet of every scope against <paramref name="cutoff"/>, deepest first. It takes no lock:
    /// a prune reads the list once and works through it in batches, each of which locks and re-checks its share.
    /// <see cref="IdleListTail"/> defines idle and the order, and gives the list's size limit.
    /// </summary>
    /// <remarks>
    /// A separate statement from <see cref="ListIdleReasonletsInScopeAsync"/>, as the run deletes are, so that neither
    /// gets a plan built for the other. This one reads every Reasonlet and every metrics row, so it joins the two
    /// tables whole, which PostgreSQL 17 did in testing with a hash join of two sequential scans.
    /// </remarks>
    [Query(IdleListHead + """
            FROM mnemosyne.reasonlets r
            LEFT JOIN mnemosyne.reasonlet_metrics m ON m.reasonlet_id = r.id
        """ + IdleListTail)]
    public partial Task<Guid[]> ListIdleReasonletsAsync(DateTime cutoff, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Lists every idle Reasonlet of the scope against <paramref name="cutoff"/>, deepest first, as
    /// <see cref="ListIdleReasonletsAsync"/> does for every scope. A lineage never spans scopes: the scoped foreign key
    /// keeps every parent in its child's scope.
    /// </summary>
    /// <remarks>
    /// The metrics table has no scope index, so a join left to the planner read every scope's metrics rows in
    /// testing. The metrics row is read instead by a primary-key probe per Reasonlet of the scope, in a
    /// <c>LATERAL</c> subquery that <c>LIMIT 1</c> keeps from being flattened, so a tenant's prune reads only its own
    /// rows, however much the other tenants hold.
    /// </remarks>
    [Query(IdleListHead + """
            FROM mnemosyne.reasonlets r
            LEFT JOIN LATERAL (
                SELECT x.last_executed_at FROM mnemosyne.reasonlet_metrics x
                WHERE x.reasonlet_id = r.id
                LIMIT 1
            ) m ON true
            WHERE r.scope = @scope
        """ + IdleListTail)]
    public partial Task<Guid[]> ListIdleReasonletsInScopeAsync(string scope, DateTime cutoff, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE SKIP LOCKED</c>, until the transaction ends, the given Reasonlets that still exist and that
    /// no other transaction holds, and answers the ids it locked. The first lock step of every prune batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SKIP LOCKED</c> means a prune never waits for a Reasonlet lock, so it cannot take part in a deadlock at this
    /// step, whatever order another writer locks Reasonlets in. A row that another transaction holds in any mode is
    /// left out: an append's <c>FOR KEY SHARE</c>, a remember's locks on its path, an erase's <c>FOR UPDATE</c>. The
    /// re-check, <see cref="StillIdleSql"/>, treats a row the batch does not hold as used, so a skipped row keeps its
    /// whole ancestry until a later prune. A row an erase has deleted is not found.
    /// </para>
    /// <para>
    /// <c>FOR UPDATE</c> conflicts with every lock a writer takes on a Reasonlet, so from here until commit no run can be
    /// appended to a locked row and no version remembered below one; a writer that comes later waits and then finds
    /// the row gone. The <c>ANY</c> filter finds the rows by primary key.
    /// </para>
    /// </remarks>
    [Query("""
        SELECT coalesce(array_agg(l.id), '{}') FROM (
            SELECT r.id
            FROM mnemosyne.reasonlets r
            WHERE r.id = ANY(@ids)
            FOR UPDATE OF r SKIP LOCKED
        ) l
        """)]
    public partial Task<Guid[]> LockIdleReasonletsAsync(Guid[] ids, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Counts the executions of the locked Reasonlets, without locking them. The store reads the count to decide
    /// whether a chunk's runs fit in its Reasonlet batch, or must first go in run batches of their own.
    /// </summary>
    /// <remarks>
    /// A plain count, because the decision needs no lock: <see cref="DeleteStillIdleAsync"/> checks the bound again on
    /// the rows it finds idle, and a run batch locks only the runs it deletes. Only the Reasonlet batch's delete needs
    /// every run locked first, by <see cref="LockIdleExecutionsAsync"/>, so that it takes no execution lock while it
    /// holds metrics rows. The executions are found by a probe of the <c>(scope, reasonlet_id)</c> index per
    /// Reasonlet, as in that method. The Reasonlet locks hold new runs off, so the count is exact for the batch.
    /// </remarks>
    [Query("""
        SELECT count(*)::int
        FROM mnemosyne.reasonlets r CROSS JOIN LATERAL (
            SELECT 1 FROM mnemosyne.reasonlet_executions x
            WHERE x.scope = r.scope AND x.reasonlet_id = r.id
            OFFSET 0
        ) c
        WHERE r.id = ANY(@ids)
        """)]
    public partial Task<int?> CountIdleExecutionsAsync(Guid[] ids, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in id order and until the transaction ends, every execution of the locked Reasonlets,
    /// and answers how many it locked. The second lock step of a Reasonlet batch, after
    /// <see cref="LockIdleReasonletsAsync"/> and before <see cref="LockIdleMetricsAsync"/>.
    /// </summary>
    /// <remarks>
    /// The lock is what the Reasonlet batch's delete needs, not the count, which nothing reads. Executions before
    /// metrics, as an erase takes them: a subject erase locks runs of a Reasonlet it keeps and then
    /// that Reasonlet's metrics row, without locking the Reasonlet, so a prune that took the metrics row first and
    /// then deleted the runs would deadlock with it. The executions are found through their Reasonlet's scope, by a
    /// probe of the <c>(scope, reasonlet_id)</c> index per Reasonlet that <c>OFFSET 0</c> keeps from being flattened,
    /// and are then locked by primary key, as in <see cref="LockAndDeleteSql"/>. Joined directly, PostgreSQL 17 hashed
    /// the batch and scanned the whole executions table in testing.
    /// </remarks>
    [Query("""
        SELECT count(*)::int FROM (
            SELECT 1
            FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(
                SELECT c.id
                FROM mnemosyne.reasonlets r CROSS JOIN LATERAL (
                    SELECT x.id FROM mnemosyne.reasonlet_executions x
                    WHERE x.scope = r.scope AND x.reasonlet_id = r.id
                    OFFSET 0
                ) c
                WHERE r.id = ANY(@ids)))
            ORDER BY e.id
            FOR UPDATE OF e
        ) l
        """)]
    public partial Task<int?> LockIdleExecutionsAsync(Guid[] ids, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks <c>FOR UPDATE</c>, in id order and until the transaction ends, the metrics rows of the locked
    /// Reasonlets, and answers how many it locked. The last lock step of a Reasonlet batch, the order an append and an
    /// erase reach a metrics row in: after the Reasonlet and after the executions.
    /// </summary>
    /// <remarks>
    /// The lock keeps the re-check's view of <c>last_executed_at</c> fixed until the delete: no append can move it,
    /// because the Reasonlet lock holds appends off, and no erase can subtract from it, because this lock holds the
    /// erase off until the batch commits.
    /// </remarks>
    [Query("""
        SELECT count(*)::int FROM (
            SELECT 1 FROM mnemosyne.reasonlet_metrics m
            WHERE m.reasonlet_id = ANY(@ids)
            ORDER BY m.reasonlet_id
            FOR UPDATE OF m
        ) l
        """)]
    public partial Task<int?> LockIdleMetricsAsync(Guid[] ids, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// The re-check: of the Reasonlets a batch has locked, the CTE <c>idle</c> holds those that are still idle against
    /// <c>@cutoff</c>, with their scopes. It is the start of each statement that deletes what a batch locked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list was read before the locks, so an append or a remember may have made a listed row used since. The
    /// re-check runs in a fresh statement after the locks, and under READ COMMITTED that statement sees every commit
    /// that landed before them, new children included. Nothing can make a locked row used after that, because
    /// every writer that could needs a lock on it or on a locked descendant.
    /// </para>
    /// <para>
    /// A locked row is still idle when every row in its subtree is held by this batch and none of them is used now.
    /// A subtree row the batch does not hold counts as used, whatever its times say: it is a row
    /// <see cref="LockIdleReasonletsAsync"/> skipped because another transaction holds it, in this batch or an
    /// earlier one, or one remembered since the list was read. So a skipped row keeps its whole ancestry, and the
    /// batch never deletes a row whose child it cannot delete with it, nor cuts a link it does not hold.
    /// </para>
    /// <para>
    /// The walk goes down from the locked rows, through the subtree as it stands, finds the used rows there, and walks
    /// up from those to mark their ancestors. Deeper listed rows went in earlier batches or are locked in this one, so
    /// the subtrees are small: what is left of them is this batch's rows, plus the rare rows that were skipped or became
    /// used. A used child therefore keeps its locked parent too, so a batch that drops a row never deletes the row's
    /// parent. Both walks use <c>UNION</c> and
    /// <c>LATERAL</c> index probes, as the list does, and each metrics row is read by a primary-key probe, so the
    /// re-check reads about what the batch holds whatever the size of the tables. Joined directly, PostgreSQL 17
    /// hashed whole sequential scans of the Reasonlets and the metrics in testing.
    /// </para>
    /// </remarks>
    private const string StillIdleSql = """
        WITH RECURSIVE locked AS MATERIALIZED (
            SELECT r.id, r.parent_id, r.scope, r.created_at FROM mnemosyne.reasonlets r WHERE r.id = ANY(@lockedIds)
        ), subtree (id, parent_id, scope, created_at) AS (
            SELECT id, parent_id, scope, created_at FROM locked
            UNION
            SELECT c.id, c.parent_id, c.scope, c.created_at
            FROM subtree CROSS JOIN LATERAL (
                SELECT r.id, r.parent_id, r.scope, r.created_at FROM mnemosyne.reasonlets r
                WHERE r.scope = subtree.scope AND r.parent_id = subtree.id
                OFFSET 0
            ) c
        ), used (id, parent_id, scope) AS (
            SELECT s.id, s.parent_id, s.scope
            FROM subtree s LEFT JOIN LATERAL (
                SELECT x.last_executed_at FROM mnemosyne.reasonlet_metrics x
                WHERE x.reasonlet_id = s.id
                LIMIT 1
            ) m ON true
            WHERE s.id <> ALL(@lockedIds) OR s.created_at >= @cutoff OR m.last_executed_at >= @cutoff
            UNION
            SELECT p.id, p.parent_id, p.scope
            FROM used CROSS JOIN LATERAL (
                SELECT r.id, r.parent_id, r.scope FROM mnemosyne.reasonlets r
                WHERE r.id = used.parent_id AND r.scope = used.scope
                LIMIT 1
            ) p
        ), idle AS MATERIALIZED (
            SELECT l.id, l.scope FROM locked l
            WHERE NOT EXISTS (SELECT 1 FROM used u WHERE u.id = l.id)
        )
        """;

    /// <summary>
    /// Re-checks the locked Reasonlets and deletes up to <paramref name="batchSize"/> executions of those still idle,
    /// and answers how many it deleted. Their feedback cascades; their metrics rows are left alone, and go with their
    /// Reasonlets.
    /// </summary>
    /// <remarks>
    /// The run batches that come before a chunk's Reasonlet batch when the chunk holds more runs than
    /// <paramref name="batchSize"/>, so that no transaction deletes more than that many executions. Each runs after
    /// <see cref="LockIdleReasonletsAsync"/> in the same transaction, so a Reasonlet that became used is left with its
    /// runs, and none can become used while the batch runs. <see cref="StillIdleSql"/> is the re-check; <see cref="LockAndDeleteSql"/> locks the executions in id
    /// order and deletes them. The lock order is Reasonlets, then executions, a prefix of every writer's order. The
    /// runs of an idle Reasonlet are all older than the cutoff, so this is the run pruning the counters already keep.
    /// </remarks>
    [Query(StillIdleSql + """
        , doomed AS (
            SELECT e.id
            FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(
                SELECT c.id
                FROM idle i CROSS JOIN LATERAL (
                    SELECT x.id FROM mnemosyne.reasonlet_executions x
                    WHERE x.scope = i.scope AND x.reasonlet_id = i.id
                    OFFSET 0
                ) c
                LIMIT @batchSize))
        """ + LockAndDeleteSql)]
    public partial Task<int> DeleteIdleExecutionsAsync(
        Guid[] lockedIds, DateTime cutoff, int batchSize, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Re-checks the locked Reasonlets and deletes those still idle, with their executions, and answers how many of
    /// each it deleted. Feedback and metrics rows cascade. Locked rows that became used are kept and not counted. When
    /// the still-idle rows hold more than <paramref name="batchSize"/> executions, it deletes that many of them instead,
    /// and no Reasonlet, and says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last statement of a Reasonlet batch, after <see cref="LockIdleReasonletsAsync"/>,
    /// <see cref="LockIdleExecutionsAsync"/> and <see cref="LockIdleMetricsAsync"/>. <see cref="StillIdleSql"/> is
    /// the re-check. Every row it deletes is one the batch holds: the Reasonlets and their metrics rows were locked,
    /// and so were their executions, because a run added after the execution lock needs the Reasonlet's lock, and a
    /// Reasonlet that gained a run is used and kept.
    /// </para>
    /// <para>
    /// The bound is checked here, on the rows the re-check keeps idle, rather than only on the count of locked runs,
    /// because the two statements see different moments: a row can become idle in between, when an erase removes the
    /// skipped descendant that kept it, and bring runs of its own. Deciding in this statement keeps every batch within
    /// <paramref name="batchSize"/> executions however the rows change, and a batch that strips runs this way leaves
    /// the Reasonlets for the next one.
    /// </para>
    /// <para>
    /// Both deletes find their rows by primary key from an array, as <see cref="LockAndDeleteSql"/> does, and the
    /// executions through a probe per Reasonlet, so neither depends on the planner choosing an index.
    /// </para>
    /// <para>
    /// The executions are deleted by this statement rather than by the cascade, so that they are counted. The
    /// cascade from the Reasonlets fires at the end of the statement and finds them gone. Deleting a Reasonlet would
    /// cut its children's link, but its children are idle, held and deeper, so they are in this batch or went in an
    /// earlier one, and a child that is used or not held keeps the Reasonlet too.
    /// </para>
    /// </remarks>
    [Query(StillIdleSql + """
        , idle_runs AS MATERIALIZED (
            SELECT c.id
            FROM idle i CROSS JOIN LATERAL (
                SELECT x.id FROM mnemosyne.reasonlet_executions x
                WHERE x.scope = i.scope AND x.reasonlet_id = i.id
                OFFSET 0
            ) c
        ), fits AS MATERIALIZED (
            SELECT count(*) <= @batchSize AS whole FROM idle_runs
        ), gone_executions AS (
            DELETE FROM mnemosyne.reasonlet_executions e
            WHERE e.id = ANY(ARRAY(SELECT r.id FROM idle_runs r LIMIT @batchSize))
            RETURNING e.id
        ), gone_reasonlets AS (
            DELETE FROM mnemosyne.reasonlets r
            WHERE (SELECT f.whole FROM fits f)
              AND r.id = ANY(ARRAY(SELECT i.id FROM idle i))
            RETURNING r.id
        )
        SELECT (SELECT count(*) FROM gone_reasonlets)::int,
               (SELECT count(*) FROM gone_executions)::int,
               NOT (SELECT f.whole FROM fits f)
        """)]
    public partial Task<IdlePruneRow?> DeleteStillIdleAsync(
        Guid[] lockedIds, DateTime cutoff, int batchSize, IAsyncDbTransaction transaction, CancellationToken ct);
}
