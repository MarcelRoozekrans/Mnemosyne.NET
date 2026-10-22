using System.Data.Async;
using Mnemosyne.NET.PostgreSql.Rows;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Reads and records the store's settings in <c>mnemosyne.settings</c>.</summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class SettingsRepository(IAsyncDbConnection connection)
{
    /// <summary>Whether the settings table exists, so a database the migrations never reached reads as not migrated rather than failing.</summary>
    [Query("SELECT to_regclass('mnemosyne.settings') IS NOT NULL")]
    public partial Task<bool> SettingsTableExistsAsync(CancellationToken ct);

    /// <summary>The server's version, which the schema's foreign keys need to be 15 or later.</summary>
    [Query("SELECT current_setting('server_version_num')::int, current_setting('server_version')")]
    public partial Task<ServerVersionRow?> GetServerVersionAsync(CancellationToken ct);

    /// <summary>The installed pgvector version, or null when the extension is missing.</summary>
    [Query("SELECT extversion FROM pg_extension WHERE extname = 'vector'")]
    public partial Task<string?> GetVectorVersionAsync(CancellationToken ct);

    [Query("SELECT value::int FROM mnemosyne.settings WHERE key = 'dimensions'")]
    public partial Task<int?> GetDimensionsAsync(CancellationToken ct);

    /// <summary>Reads every recorded setting as one row; a setting that was never recorded reads as null.</summary>
    [Query("""
        SELECT (SELECT value::int FROM mnemosyne.settings WHERE key = 'dimensions'),
               (SELECT value::int FROM mnemosyne.settings WHERE key = 'hnsw_m'),
               (SELECT value::int FROM mnemosyne.settings WHERE key = 'hnsw_ef_construction')
        """)]
    public partial Task<SettingsRow?> GetAsync(IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Serializes concurrent MigrateAsync calls, so only one of them applies the dimension.</summary>
    [Command("LOCK TABLE mnemosyne.settings IN SHARE ROW EXCLUSIVE MODE")]
    public partial Task LockAsync(IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Records what the first migration applied: the dimension and the HNSW index options.</summary>
    [Command("""
        INSERT INTO mnemosyne.settings (key, value)
        VALUES ('dimensions', @dimensions::text), ('hnsw_m', @hnswM::text), ('hnsw_ef_construction', @hnswEfConstruction::text)
        """)]
    public partial Task RecordAsync(int dimensions, int hnswM, int hnswEfConstruction, IAsyncDbTransaction transaction, CancellationToken ct);
}
