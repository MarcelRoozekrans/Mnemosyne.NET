namespace Mnemosyne.NET.PostgreSql;

/// <summary>Checks the installed pgvector version against the one the store needs.</summary>
internal static class PgVectorVersion
{
    /// <summary>The oldest pgvector with iterative index scans, which scoped search relies on.</summary>
    internal const string Minimum = "0.8.0";

    /// <summary>
    /// Whether <paramref name="found"/> is at least <see cref="Minimum"/>, compared numerically so that
    /// <c>0.10.0</c> is newer than <c>0.8.0</c>. A version that does not read as dotted numbers is not supported.
    /// </summary>
    internal static bool IsSupported(string? found) =>
        Version.TryParse(found, out var version) && version >= Version.Parse(Minimum);
}
