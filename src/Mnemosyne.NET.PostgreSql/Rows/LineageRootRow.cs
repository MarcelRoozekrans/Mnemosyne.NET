namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>The top of the lineage of one Reasonlet, read by position.</summary>
/// <param name="StartId">The Reasonlet the walk started from.</param>
/// <param name="RootId">The Reasonlet at the top of its lineage, whose <c>parent_id</c> is null.</param>
internal sealed record LineageRootRow(Guid StartId, Guid RootId);
