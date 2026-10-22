namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>The highest minor in a lineage, read by position.</summary>
/// <param name="MaxMinor">The highest minor among the well-formed <c>1.k</c> versions, or null when there are none.</param>
/// <param name="Malformed">A version that is not of the form <c>1.k</c>, or null when every version is.</param>
internal sealed record LineageMinorRow(decimal? MaxMinor, string? Malformed);
