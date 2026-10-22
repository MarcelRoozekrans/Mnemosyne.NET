namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>What an erasure's execution delete removed, read by position.</summary>
/// <param name="Count">How many executions it deleted.</param>
/// <param name="Recount">The Reasonlets it keeps that lost executions, whose metrics need a recount.</param>
internal sealed record ErasedExecutionsRow(int Count, Guid[] Recount);
