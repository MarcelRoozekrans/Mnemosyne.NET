namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>The columns of a locked parent that a remember decides on, read by position.</summary>
/// <param name="Id">The parent's id.</param>
/// <param name="Version">The parent's version label.</param>
/// <param name="ContentHash">The content hash stored when the parent was inserted.</param>
/// <param name="Status">The parent's <c>ReasonletStatus</c> number, reported by an execution-only receipt.</param>
internal sealed record ParentRow(Guid Id, string Version, string ContentHash, short Status);
