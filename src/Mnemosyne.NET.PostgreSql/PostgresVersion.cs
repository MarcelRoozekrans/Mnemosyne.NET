namespace Mnemosyne.NET.PostgreSql;

/// <summary>Checks the PostgreSQL server version against the one the schema needs.</summary>
internal static class PostgresVersion
{
    /// <summary>The oldest PostgreSQL whose <c>ON DELETE SET NULL</c> takes a column list, which the scoped self-reference needs.</summary>
    internal const int MinimumMajor = 15;

    /// <summary>Whether a <c>server_version_num</c> such as 170011 is at least <see cref="MinimumMajor"/>.</summary>
    internal static bool IsSupported(int serverVersionNum) => serverVersionNum >= MinimumMajor * 10000;
}
