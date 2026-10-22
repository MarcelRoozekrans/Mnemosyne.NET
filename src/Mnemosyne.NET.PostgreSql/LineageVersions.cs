using System.Globalization;

namespace Mnemosyne.NET.PostgreSql;

internal static class LineageVersions
{
    internal const string Initial = "1.0";

    /// <summary>Returns the minor part of a <c>1.k</c> version. A malformed version is a bug, so it throws.</summary>
    internal static int Minor(string version)
    {
        if (version.StartsWith("1.", StringComparison.Ordinal)
            && int.TryParse(version.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return minor;
        }

        throw new FormatException($"'{version}' is not a lineage version of the form 1.k.");
    }

    /// <summary>Returns <c>1.k</c> where k is one more than the highest minor in the lineage.</summary>
    internal static string Next(IEnumerable<string> lineageVersions)
    {
        var max = 0;
        foreach (var version in lineageVersions)
        {
            max = Math.Max(max, Minor(version));
        }

        return string.Create(CultureInfo.InvariantCulture, $"1.{max + 1}");
    }
}
