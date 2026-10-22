using System.Data.Async;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Reads and records the store's settings in <c>mnemosyne.settings</c>.</summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class SettingsRepository(IAsyncDbConnection connection)
{
    /// <summary>Whether the settings table exists, so a database the migrations never reached reads as not migrated rather than failing.</summary>
    [Query("SELECT to_regclass('mnemosyne.settings') IS NOT NULL")]
    public partial Task<bool> SettingsTableExistsAsync(CancellationToken ct);

    /// <summary>The installed pgvector version, or null when the extension is missing.</summary>
    [Query("SELECT extversion FROM pg_extension WHERE extname = 'vector'")]
    public partial Task<string?> GetVectorVersionAsync(CancellationToken ct);

    [Query("SELECT value::int FROM mnemosyne.settings WHERE key = 'dimensions'")]
    public partial Task<int?> GetDimensionsAsync(CancellationToken ct);

    [Query("SELECT value::int FROM mnemosyne.settings WHERE key = 'dimensions'")]
    public partial Task<int?> GetDimensionsAsync(IAsyncDbTransaction transaction, CancellationToken ct);

    /// <summary>Serializes concurrent MigrateAsync calls, so only one of them applies the dimension.</summary>
    [Command("LOCK TABLE mnemosyne.settings IN SHARE ROW EXCLUSIVE MODE")]
    public partial Task LockAsync(IAsyncDbTransaction transaction, CancellationToken ct);

    [Command("INSERT INTO mnemosyne.settings (key, value) VALUES ('dimensions', @dimensions::text)")]
    public partial Task RecordDimensionsAsync(int dimensions, IAsyncDbTransaction transaction, CancellationToken ct);
}
