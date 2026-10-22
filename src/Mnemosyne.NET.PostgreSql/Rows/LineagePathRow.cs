namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>One step of a walk up <c>parent_id</c>, read by position.</summary>
/// <param name="Id">The Reasonlet at this step.</param>
/// <param name="IsCycle">Whether the walk stopped here because it met a Reasonlet it had already visited.</param>
internal sealed record LineagePathRow(Guid Id, bool IsCycle);
