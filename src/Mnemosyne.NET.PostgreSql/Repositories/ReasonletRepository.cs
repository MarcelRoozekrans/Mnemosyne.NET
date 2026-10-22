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
        SELECT id, version, content_hash, root_id, status
        FROM mnemosyne.reasonlets
        WHERE scope = @scope AND id = @id
        FOR UPDATE
        """)]
    public partial Task<ParentRow?> LockParentAsync(string scope, Guid id, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Locks a lineage's root until the transaction ends, which serializes every new version in the lineage. The
    /// root is found by id alone: it shares the scope of the locked parent that named it. Locking a root the
    /// transaction already holds, because the parent is the root, is a no-op.
    /// </summary>
    [Command("SELECT 1 FROM mnemosyne.reasonlets WHERE id = @rootId FOR UPDATE")]
    public partial Task LockRootAsync(Guid rootId, IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Reads every version in a lineage, as one array: the root and every row whose root it is.</summary>
    [Query("SELECT coalesce(array_agg(version), '{}') FROM mnemosyne.reasonlets WHERE id = @rootId OR root_id = @rootId")]
    public partial Task<string[]> GetLineageVersionsAsync(Guid rootId, IAsyncDbTransaction transaction, CancellationToken ct);

    [Command("""
        INSERT INTO mnemosyne.reasonlets
            (id, scope, name, intent, version, graph, content_hash, parent_id, root_id, status,
             source, run_id, subject_id, tags, created_at, embedding)
        VALUES
            (@id, @scope, @name, @intent, @version, @graph, @contentHash, @parentId, @rootId, @status,
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
        Guid? rootId,
        short status,
        string source,
        string? runId,
        string? subjectId,
        [Json] string? tags,
        float[] embedding,
        IAsyncDbTransaction transaction,
        CancellationToken ct);
}
