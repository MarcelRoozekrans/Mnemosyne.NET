using Mnemosyne.NET.Abstractions;
using Npgsql;

namespace Mnemosyne.NET.PostgreSql;

internal static class PostgresErrors
{
    /// <summary>Maps a database exception to a <see cref="MnemosyneError"/>, or null when it is a bug that must propagate.</summary>
    internal static MnemosyneError? Map(Exception ex)
    {
        if (ex is not NpgsqlException npgsql)
        {
            return null;
        }

        var state = npgsql.SqlState;
        if (state is "40001" or "40P01" or "23503")
        {
            return new MnemosyneError(MnemosyneErrorCode.Conflict, "The write conflicted with a concurrent change.", ex.Message);
        }

        if (state is null || state.StartsWith("08", StringComparison.Ordinal) || state.StartsWith("57P", StringComparison.Ordinal))
        {
            return new MnemosyneError(MnemosyneErrorCode.Unavailable, "The PostgreSQL store is unavailable.", ex.Message);
        }

        return null;
    }
}
