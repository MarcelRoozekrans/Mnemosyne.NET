namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>The end of a walk up <c>parent_id</c>, read by position.</summary>
/// <param name="Id">The farthest Reasonlet the walk reached: the lineage's top, unless the walk met a cycle.</param>
/// <param name="IsCycle">Whether the walk stopped because it met a Reasonlet it had already visited.</param>
internal sealed record LineageTopRow(Guid Id, bool IsCycle);
