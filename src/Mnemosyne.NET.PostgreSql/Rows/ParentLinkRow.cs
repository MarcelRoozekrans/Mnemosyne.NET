namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>One <c>parent_id</c> link met by a walk up a lineage, read by position.</summary>
/// <param name="Id">The Reasonlet.</param>
/// <param name="ParentId">Its parent, or null when it is the top of its lineage.</param>
internal sealed record ParentLinkRow(Guid Id, Guid? ParentId);
