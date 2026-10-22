namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>What the first <c>MigrateAsync</c> recorded, read by position. Each is null until it is recorded.</summary>
/// <param name="Dimensions">The embedding dimension.</param>
/// <param name="HnswM">The HNSW index's <c>m</c>.</param>
/// <param name="HnswEfConstruction">The HNSW index's <c>ef_construction</c>.</param>
internal sealed record SettingsRow(int? Dimensions, int? HnswM, int? HnswEfConstruction);
