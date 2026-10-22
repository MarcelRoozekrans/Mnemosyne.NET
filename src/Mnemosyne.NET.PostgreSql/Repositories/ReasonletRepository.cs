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
    /// The nearest Active Reasonlets of a scope to an embedding, nearest first, each with its similarity and execution
    /// metrics. The <c>ORDER BY</c> uses the bare distance operator so the HNSW index can serve it. The counts are cast
    /// to <c>int</c> because <c>count</c> answers <c>bigint</c>. A null intent applies no intent filter. Run it in a
    /// transaction that has set the iterative-scan settings, or the index scan can return fewer rows than the limit.
    /// </summary>
    [Query("""
        SELECT r.id, r.scope, r.name, r.intent, r.version, r.graph, r.parent_id, r.status,
               r.source, r.run_id, r.subject_id, r.tags, r.created_at,
               greatest(0, 1 - (r.embedding <=> @embedding::vector)) AS similarity,
               m.runs::int, m.successes::int, m.last_success_at
        FROM mnemosyne.reasonlets r
        LEFT JOIN LATERAL (SELECT count(*) AS runs, count(*) FILTER (WHERE success) AS successes,
                                  max(executed_at) FILTER (WHERE success) AS last_success_at
                           FROM mnemosyne.reasonlet_executions e WHERE e.reasonlet_id = r.id) m ON true
        WHERE r.scope = @scope AND r.status = 1 AND (@intent::text IS NULL OR r.intent = @intent)
        ORDER BY r.embedding <=> @embedding::vector
        LIMIT @limit
        """)]
    public partial Task<List<CandidateRow>> SearchAsync(
        string scope, float[] embedding, string? intent, int limit, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Locks a parent in its scope until the transaction ends. A parent in another scope reads as missing.</summary>
    [Query("""
        SELECT id, version, content_hash, status
        FROM mnemosyne.reasonlets
        WHERE scope = @scope AND id = @id
        FOR UPDATE
        """)]
    public partial Task<ParentRow?> LockParentAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Walks up <c>parent_id</c> within the scope from a Reasonlet to the top of its lineage, and returns the path
    /// in order: the Reasonlet first and the top, whose <c>parent_id</c> is null, last. A <c>parent_id</c> cycle,
    /// which only hand-written SQL can create, stops the walk at the first repeated row, which comes last with
    /// <see cref="LineagePathRow.IsCycle"/> set. Empty only when the Reasonlet is not in the scope.
    /// </summary>
    [Query("""
        WITH RECURSIVE up (id, parent_id, depth) AS (
            SELECT id, parent_id, 0 FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @id
            UNION ALL
            SELECT r.id, r.parent_id, up.depth + 1
            FROM mnemosyne.reasonlets r JOIN up ON r.id = up.parent_id
            WHERE r.scope = @scope
        ) CYCLE id SET is_cycle USING path
        SELECT id, is_cycle FROM up ORDER BY depth
        """)]
    public partial Task<List<LineagePathRow>> FindLineagePathAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Takes <c>FOR KEY SHARE</c> on an ancestor in its scope until the transaction ends. That blocks a
    /// <c>DELETE</c> of the ancestor, so an erase cannot cut the path to the top while this transaction runs,
    /// yet it lets other remembers share the ancestor. Answers null when the ancestor is gone: a concurrent
    /// erase deleted it after the walk read it.
    /// </summary>
    [Query("SELECT 1 FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @id FOR KEY SHARE")]
    public partial Task<int?> LockAncestorAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks a lineage's top in its scope until the transaction ends, which serializes every new version in the
    /// lineage. Locking a top the transaction already holds, because the parent is the top, is a no-op. Answers
    /// null when the top is gone: a concurrent erase deleted it after it was found.
    /// </summary>
    [Query("SELECT 1 FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @topId FOR UPDATE")]
    public partial Task<int?> LockLineageTopAsync(string scope, Guid topId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Reads every version in a lineage, as one array: the top and its whole subtree within the scope.</summary>
    /// <remarks>
    /// One array column rather than a list of rows: ZeroAlloc.ORM 2.7.0 generates no body for a
    /// <c>Task&lt;List&lt;string&gt;&gt;</c> scalar list. Return a list once
    /// https://github.com/ZeroAlloc-Net/ZeroAlloc.ORM/issues/400 ships. The <c>CYCLE</c> clause ends a walk that
    /// meets a <c>parent_id</c> cycle, and the repeated row it marks is left out. A real top has no parent, so no
    /// cycle is reachable below it; the guard only keeps hand-written corruption from looping forever.
    /// </remarks>
    [Query("""
        WITH RECURSIVE down (id, version) AS (
            SELECT id, version FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @topId
            UNION ALL
            SELECT r.id, r.version
            FROM mnemosyne.reasonlets r JOIN down ON r.parent_id = down.id
            WHERE r.scope = @scope
        ) CYCLE id SET is_cycle USING path
        SELECT coalesce(array_agg(version) FILTER (WHERE NOT is_cycle), '{}') FROM down
        """)]
    public partial Task<string[]> GetLineageVersionsAsync(string scope, Guid topId, IAsyncDbTransaction transaction, CancellationToken ct);

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
    /// every row below them. A top is a row whose parent is null or outside the scope. The walk up's <c>UNION</c>
    /// also ends at a hand-written <c>parent_id</c> cycle; no top leads into a cycle, so the walk down ends too, and
    /// rows on a cycle, which no walk down reaches, lock at depth 0.
    /// </para>
    /// <para>
    /// Two other kinds of corrupt data, which only hand-written SQL can create, fall outside these locks. A row in
    /// another scope whose <c>parent_id</c> points at a deleted Reasonlet is cut by <c>ON DELETE SET NULL</c>
    /// without being locked first. An execution whose scope differs from its Reasonlet's is deleted by
    /// <c>ON DELETE CASCADE</c> but not counted, because the execution delete filters on the erased scope.
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
            SELECT r.id, r.parent_id
            FROM mnemosyne.reasonlets r JOIN up ON r.id = up.parent_id
            WHERE r.scope = @scope
        ), tops AS (
            SELECT u.id FROM up u
            WHERE NOT EXISTS (SELECT 1 FROM mnemosyne.reasonlets p WHERE p.id = u.parent_id AND p.scope = @scope)
        ), down (id, depth) AS (
            SELECT id, 0 FROM tops
            UNION ALL
            SELECT r.id, down.depth + 1
            FROM mnemosyne.reasonlets r JOIN down ON r.parent_id = down.id
            WHERE r.scope = @scope
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
