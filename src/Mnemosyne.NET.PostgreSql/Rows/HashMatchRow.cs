namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>The columns of a locked Reasonlet whose content hash a parentless remember matched, read by position.</summary>
/// <param name="Id">The Reasonlet's id.</param>
/// <param name="Version">The Reasonlet's version label.</param>
/// <param name="Status">The Reasonlet's <c>ReasonletStatus</c> number, reported by an execution-only receipt.</param>
internal sealed record HashMatchRow(Guid Id, string Version, short Status);
