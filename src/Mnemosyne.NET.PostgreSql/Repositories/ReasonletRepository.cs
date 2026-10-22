using System.Data.Async;
using Mnemosyne.NET.PostgreSql.Rows;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Reads, locks and inserts rows of <c>mnemosyne.reasonlets</c>.</summary>
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

    /// <summary>Locks a parent in its scope until the transaction ends. A parent in another scope reads as missing.</summary>
    [Query("""
        SELECT id, version, content_hash, status
        FROM mnemosyne.reasonlets
        WHERE scope = @scope AND id = @id
        FOR UPDATE
        """)]
    public partial Task<ParentRow?> LockParentAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Finds the top of a Reasonlet's lineage by walking up <c>parent_id</c> within the scope: the farthest
    /// ancestor, which is the one whose <c>parent_id</c> is null, or the Reasonlet itself when it has no parent.
    /// A <c>parent_id</c> cycle, which only hand-written SQL can create, stops the walk at the first repeated row
    /// and comes back with <see cref="LineageTopRow.IsCycle"/> set. Null only when the Reasonlet is not in the scope.
    /// </summary>
    /// <remarks>
    /// Nullable although a locked parent always yields a row: ZeroAlloc.ORM 2.7.0 generates no body for a
    /// non-nullable single-row <c>Task&lt;T&gt;</c>. Make it non-nullable, and drop the caller's <c>!</c>, once
    /// https://github.com/ZeroAlloc-Net/ZeroAlloc.ORM/issues/400 ships.
    /// </remarks>
    [Query("""
        WITH RECURSIVE up (id, parent_id, depth) AS (
            SELECT id, parent_id, 0 FROM mnemosyne.reasonlets WHERE scope = @scope AND id = @id
            UNION ALL
            SELECT r.id, r.parent_id, up.depth + 1
            FROM mnemosyne.reasonlets r JOIN up ON r.id = up.parent_id
            WHERE r.scope = @scope
        ) CYCLE id SET is_cycle USING path
        SELECT id, is_cycle FROM up ORDER BY depth DESC LIMIT 1
        """)]
    public partial Task<LineageTopRow?> FindLineageTopAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

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
