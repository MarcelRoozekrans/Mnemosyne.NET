namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>What one batch of idle Reasonlets deleted, read by position.</summary>
/// <param name="Reasonlets">The Reasonlets deleted: the locked ones still idle at the re-check.</param>
/// <param name="Executions">Their executions deleted with them.</param>
/// <param name="Stripped">
/// Whether the still-idle Reasonlets held more executions than one batch deletes, so the batch deleted a batch of
/// their executions and no Reasonlet, and the chunk needs another batch.
/// </param>
internal sealed record IdlePruneRow(int Reasonlets, int Executions, bool Stripped);
