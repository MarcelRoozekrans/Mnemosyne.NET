using System.Data.Async;
using Mnemosyne.NET.PostgreSql.Rows;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Reads, locks, inserts and deletes rows of <c>mnemosyne.reasonlets</c>.</summary>
/// <remarks>Every select names its columns: Npgsql cannot read the <c>vector</c> embedding column into a CLR type.</remarks>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class ReasonletRepository(IAsyncDbConnection connection)
{
    [Query("""
        SELECT id, scope, name, intent, version, graph, parent_id, status, source, run_id, subject_id, tags, created_at
        FROM mnemosyne.reasonlets
        WHERE scope = @scope AND id = @id
        """)]
    public partial Task<ReasonletRow?> GetAsync(string scope, Guid id, CancellationToken ct);

    /// <summary>
    /// The nearest Active Reasonlets of a scope to an embedding, sorted by distance, each with its similarity and
    /// execution metrics. The <c>ORDER BY</c> uses the bare distance operator so the HNSW index can serve it. The
    /// metrics come from the Reasonlet's counter row; a Reasonlet without one, which the store never leaves, reads as
    /// no runs and its last execution as its creation time. The parent's metrics come from a left join on the parent's
    /// counter row in the same scope, and are null for a root, a cut lineage link or a parent without a counter row. A null intent applies no intent filter. Run it in a transaction that has set the iterative-scan
    /// settings, or the index scan can return fewer rows than the limit.
    /// </summary>
    [Query("""
        SELECT r.id, r.scope, r.name, r.intent, r.version, r.graph, r.parent_id, r.status,
               r.source, r.run_id, r.subject_id, r.tags, r.created_at,
               greatest(0, 1 - (r.embedding <=> @embedding::vector)) AS similarity,
               coalesce(m.runs, 0), coalesce(m.successes, 0), m.last_success_at,
               coalesce(m.last_executed_at, r.created_at),
               pm.runs, pm.successes, pm.last_success_at, pm.last_executed_at
        FROM mnemosyne.reasonlets r
        LEFT JOIN mnemosyne.reasonlet_metrics m ON m.reasonlet_id = r.id AND m.scope = r.scope
        LEFT JOIN mnemosyne.reasonlet_metrics pm ON pm.reasonlet_id = r.parent_id AND pm.scope = r.scope
        WHERE r.scope = @scope AND r.status = 1 AND (@intent::text IS NULL OR r.intent = @intent)
        ORDER BY r.embedding <=> @embedding::vector
        LIMIT @limit
        """)]
    public partial Task<List<CandidateRow>> SearchAsync(
        string scope, float[] embedding, string? intent, int limit, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks a parent in its scope <c>FOR NO KEY UPDATE</c> until the transaction ends. A parent in another scope reads
    /// as missing. The mode serializes remembers under one parent and conflicts with an erase's <c>FOR UPDATE</c>,
    /// yet lets the <c>FOR KEY SHARE</c> of an appended execution or of a foreign-key check through.
    /// </summary>
    [Query("""
        SELECT id, version, content_hash, status
        FROM mnemosyne.reasonlets
        WHERE scope = @scope AND id = @id
        FOR NO KEY UPDATE
        """)]
    public partial Task<ParentRow?> LockParentAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Walks up <c>parent_id</c> within the scope from a Reasonlet to the top of its lineage, and returns every link
    /// it met, in no particular order: the caller follows them from the Reasonlet. Empty only when the Reasonlet is not
    /// in the scope.
    /// </summary>
    /// <remarks>
    /// <c>UNION</c> rather than <c>UNION ALL</c> drops a link the walk has already returned, so a <c>parent_id</c>
    /// cycle, which only hand-written SQL can create, ends the walk after one lap. Nothing carries a path, and each
    /// step is one primary-key probe, so the walk is linear in the depth of the lineage. The step is a
    /// <c>LATERAL</c> subquery that <c>LIMIT 1</c> keeps from being flattened into a join: without statistics on
    /// the table the planner otherwise hashes the whole scope at every step, which is quadratic in the depth.
    /// </remarks>
    [Query("""
        WITH RECURSIVE up (id, parent_id) AS (
            SELECT id, parent_id FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @id
            UNION
            SELECT p.id, p.parent_id
            FROM up CROSS JOIN LATERAL (
                SELECT r.id, r.parent_id FROM mnemosyne.reasonlets r
                WHERE r.id = up.parent_id AND r.scope = @scope
                LIMIT 1
            ) p
        )
        SELECT id, parent_id FROM up
        """)]
    public partial Task<List<ParentLinkRow>> WalkUpAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Takes <c>FOR KEY SHARE</c> on the ancestors in their scope until the transaction ends, one after another in the
    /// order given, and answers how many it locked: fewer than given when a concurrent erase deleted one after the
    /// walk read it. The lock blocks a <c>DELETE</c> of an ancestor, so an erase cannot cut the path to the top while
    /// this transaction runs, yet it lets other remembers share the ancestor.
    /// </summary>
    /// <remarks>
    /// The rows are locked in the order of <paramref name="ids"/>: the <c>LockRows</c> step of the plan sits above the
    /// sort by ordinality and locks each row as the sort hands it over. A row whose lock waits is re-checked when the
    /// lock is granted and left out when it has been deleted.
    /// </remarks>
    [Query("""
        SELECT count(*)::int FROM (
            SELECT 1 FROM unnest(@ids::uuid[]) WITH ORDINALITY u (id, ord)
            JOIN mnemosyne.reasonlets r ON r.id = u.id AND r.scope = @scope
            ORDER BY u.ord
            FOR KEY SHARE OF r
        ) locked
        """)]
    public partial Task<int?> LockAncestorsAsync(string scope, Guid[] ids, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks a lineage's top in its scope <c>FOR NO KEY UPDATE</c> until the transaction ends, which serializes every
    /// new version in the lineage. Locking a top the transaction already holds, because the parent is the top, is a
    /// no-op. Answers null when the top is gone: a concurrent erase deleted it after it was found.
    /// </summary>
    [Query("SELECT 1 FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @topId FOR NO KEY UPDATE")]
    public partial Task<int?> LockLineageTopAsync(string scope, Guid topId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Reads the highest minor in a lineage, the top and its whole subtree within the scope, and one version that is
    /// not of the form <c>1.k</c> when there is one.
    /// </summary>
    /// <remarks>
    /// The minor is read as <c>numeric</c>, so no stored version overflows the read; the caller adds one with checked
    /// arithmetic. <c>UNION</c> ends the walk at a row it has already returned, nothing carries a path, and each step
    /// is one probe of the <c>(scope, parent_id)</c> index, kept from being flattened into a hash join by
    /// <c>OFFSET 0</c>, so it is linear in the size of the lineage. A real top has no parent, so no cycle is
    /// reachable below it.
    /// </remarks>
    [Query("""
        WITH RECURSIVE down (id, version) AS (
            SELECT id, version FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @topId
            UNION
            SELECT c.id, c.version
            FROM down CROSS JOIN LATERAL (
                SELECT r.id, r.version FROM mnemosyne.reasonlets r
                WHERE r.parent_id = down.id AND r.scope = @scope
                OFFSET 0
            ) c
        )
        SELECT max(CASE WHEN version ~ '^1[.](0|[1-9][0-9]*)$' THEN split_part(version, '.', 2)::numeric END),
               min(CASE WHEN version !~ '^1[.](0|[1-9][0-9]*)$' THEN version END)
        FROM down
        """)]
    public partial Task<LineageMinorRow?> GetLineageMinorAsync(string scope, Guid topId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks, until the transaction ends, what an erasure changes in the scope: the Reasonlets it deletes, every one
    /// or the subject's, and their children that it keeps, whose <c>parent_id</c> the delete cuts. Answers the ids
    /// to delete. A null subject selects the whole scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows are locked deepest first, by their depth below the top of their lineage in the scope, and by id
    /// within a depth. A remember takes its path locks bottom-up and only ever waits for a row above every row it
    /// holds, so an erase that locks in the same direction waits behind it rather than deadlocking with it, and two
    /// erases lock overlapping rows in one order. <c>FOR UPDATE</c> above an <c>ORDER BY</c> locks the rows in
    /// sorted order. A row a concurrent erase deleted first is skipped, so it is neither returned nor counted twice.
    /// Holding the deleted rows blocks every new child and execution of them until commit.
    /// </para>
    /// <para>
    /// Each depth is computed once, so the work grows linearly with the lineages, however deep: a walk up with a
    /// <c>UNION</c> finds the tops above the targets, visiting each row once, and a walk down from those tops numbers
    /// every row below them. Each step of either walk is an index probe in a <c>LATERAL</c> subquery that
    /// <c>LIMIT 1</c> or <c>OFFSET 0</c> keeps from being flattened: without statistics on the table the planner
    /// otherwise hashes the whole scope at every step of a deep chain, which is quadratic in its depth. A top is a row whose parent is null; the scoped foreign key keeps every parent in its
    /// child's scope. The walk up's <c>UNION</c> also ends at a hand-written <c>parent_id</c> cycle; no top leads into
    /// a cycle, so the walk down ends too, and rows on or below a cycle, which no walk down reaches, lock at depth 0.
    /// </para>
    /// </remarks>
    [Query("""
        WITH RECURSIVE doomed AS (
            SELECT id FROM mnemosyne.reasonlets
            WHERE scope = @scope AND (@subjectId::text IS NULL OR subject_id = @subjectId)
        ), target (id) AS (
            SELECT id FROM doomed
            UNION
            SELECT c.id FROM mnemosyne.reasonlets c JOIN doomed d ON c.parent_id = d.id
            WHERE c.scope = @scope
        ), up (id, parent_id) AS (
            SELECT r.id, r.parent_id FROM mnemosyne.reasonlets r JOIN target t ON r.id = t.id
            UNION
            SELECT p.id, p.parent_id
            FROM up CROSS JOIN LATERAL (
                SELECT r.id, r.parent_id FROM mnemosyne.reasonlets r
                WHERE r.id = up.parent_id AND r.scope = @scope
                LIMIT 1
            ) p
        ), tops AS (
            SELECT id FROM up WHERE parent_id IS NULL
        ), down (id, depth) AS (
            SELECT id, 0 FROM tops
            UNION ALL
            SELECT c.id, down.depth + 1
            FROM down CROSS JOIN LATERAL (
                SELECT r.id FROM mnemosyne.reasonlets r
                WHERE r.parent_id = down.id AND r.scope = @scope
                OFFSET 0
            ) c
        ), locked AS (
            SELECT r.id
            FROM mnemosyne.reasonlets r JOIN target t ON r.id = t.id LEFT JOIN down d ON r.id = d.id
            ORDER BY coalesce(d.depth, 0) DESC, r.id
            FOR UPDATE OF r
        )
        SELECT coalesce(array_agg(l.id) FILTER (WHERE l.id IN (SELECT id FROM doomed)), '{}') FROM locked l
        """)]
    public partial Task<Guid[]> LockForErasureAsync(string scope, string? subjectId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Deletes Reasonlets of the scope that <see cref="LockForErasureAsync"/> locked, and answers how many it deleted.
    /// Their children's <c>parent_id</c> is cut by <c>ON DELETE SET NULL</c>, which the count leaves out.
    /// </summary>
    [Command("DELETE FROM mnemosyne.reasonlets WHERE scope = @scope AND id = ANY(@ids)")]
    public partial Task<int> DeleteAsync(string scope, Guid[] ids, IAsyncDbTransaction transaction, CancellationToken ct);

    [Command("""
        INSERT INTO mnemosyne.reasonlets
            (id, scope, name, intent, version, graph, content_hash, parent_id, status,
             source, run_id, subject_id, tags, created_at, embedding)
        VALUES
            (@id, @scope, @name, @intent, @version, @graph, @contentHash, @parentId, @status,
             @source, @runId, @subjectId, @tags, now(), @embedding::vector)
        """)]
    public partial Task InsertAsync(
        Guid id,
        string scope,
        string name,
        string intent,
        string version,
        string graph,
        string contentHash,
        Guid? parentId,
        short status,
        string source,
        string? runId,
        string? subjectId,
        [Json] string? tags,
        float[] embedding,
        IAsyncDbTransaction transaction,
        CancellationToken ct);
}
