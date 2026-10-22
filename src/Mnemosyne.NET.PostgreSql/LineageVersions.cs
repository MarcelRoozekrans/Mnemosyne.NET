using System.Globalization;

namespace Mnemosyne.NET.PostgreSql;

internal static class LineageVersions
{
    internal const string Initial = "1.0";

    /// <summary>
    /// Returns <c>1.k</c> where k is one more than <paramref name="maxMinor"/>, the highest minor in the lineage, or
    /// <c>1.1</c> when there is none. A <paramref name="malformed"/> version in the lineage is a bug, so it throws
    /// <see cref="FormatException"/>; a next minor past <see cref="int.MaxValue"/> throws <see cref="OverflowException"/>.
    /// </summary>
    /// <param name="maxMinor">The highest minor among the lineage's well-formed versions, or null when there are none.</param>
    /// <param name="malformed">A version of the lineage that is not of the form <c>1.k</c>, or null when every version is.</param>
    internal static string Next(decimal? maxMinor, string? malformed)
    {
        if (malformed is not null)
        {
            throw new FormatException($"'{malformed}' is not a lineage version of the form 1.k.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"1.{checked((int)(maxMinor ?? 0) + 1)}");
    }
}
