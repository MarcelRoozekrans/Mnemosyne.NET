using Mnemosyne.NET.Abstractions;
using Npgsql;

namespace Mnemosyne.NET.PostgreSql;

internal static class PostgresErrors
{
    /// <summary>Maps a database exception to a <see cref="MnemosyneError"/>, or null when it is a bug that must propagate.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Conflict</c>, worth a retry: serialization failure <c>40001</c>, deadlock <c>40P01</c>, a foreign-key
    /// violation <c>23503</c> from a concurrent erase, and lock not available <c>55P03</c>.</item>
    /// <item><c>Unavailable</c>, the server cannot serve now: connection exceptions <c>08*</c>, insufficient resources
    /// <c>53*</c>, operator intervention <c>57P*</c>, query canceled or statement timeout <c>57014</c>, a read-only
    /// transaction <c>25006</c> such as a standby, and a failure with no SQL state at all.</item>
    /// </list>
    /// </remarks>
    internal static MnemosyneError? Map(Exception ex)
    {
        if (ex is not NpgsqlException npgsql)
        {
            return null;
        }

        var state = npgsql.SqlState;
        if (state is "40001" or "40P01" or "23503" or "55P03")
        {
            return new MnemosyneError(MnemosyneErrorCode.Conflict, "The write conflicted with a concurrent change.", ex.Message);
        }

        if (state is null or "57014" or "25006"
            || state.StartsWith("08", StringComparison.Ordinal)
            || state.StartsWith("53", StringComparison.Ordinal)
            || state.StartsWith("57P", StringComparison.Ordinal))
        {
            return new MnemosyneError(MnemosyneErrorCode.Unavailable, "The PostgreSQL store is unavailable.", ex.Message);
        }

        return null;
    }
}
