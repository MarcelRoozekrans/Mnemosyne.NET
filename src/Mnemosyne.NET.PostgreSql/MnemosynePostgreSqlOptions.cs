namespace Mnemosyne.NET.PostgreSql;

/// <summary>Settings for the PostgreSQL + pgvector Reasonlet store.</summary>
public sealed class MnemosynePostgreSqlOptions
{
    private const int MaxDimensions = 2000;

    /// <summary>
    /// The embedding dimension, fixed when the schema is created. Must be from 1 to 2000, the most
    /// that a pgvector HNSW index can hold.
    /// </summary>
    public required int Dimensions { get; init; }

    /// <summary>The HNSW <c>m</c> parameter, from 2 to 100. Defaults to pgvector's 16.</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>The HNSW <c>ef_construction</c> parameter, from 4 to 1000. Defaults to pgvector's 64.</summary>
    public int HnswEfConstruction { get; init; } = 64;

    internal void Validate()
    {
        if (Dimensions is < 1 or > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Dimensions), Dimensions, $"{nameof(Dimensions)} must be from 1 to {MaxDimensions}, the most that an HNSW index can hold.");
        }

        if (HnswM is < 2 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(HnswM), HnswM, $"{nameof(HnswM)} must be from 2 to 100.");
        }

        if (HnswEfConstruction is < 4 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HnswEfConstruction), HnswEfConstruction, $"{nameof(HnswEfConstruction)} must be from 4 to 1000.");
        }
    }
}
