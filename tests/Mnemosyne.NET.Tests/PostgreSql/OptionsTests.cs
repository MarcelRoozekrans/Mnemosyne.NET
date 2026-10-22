using Mnemosyne.NET.PostgreSql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class OptionsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2000)]
    public void ValidDimensionsPass(int dimensions) =>
        new MnemosynePostgreSqlOptions { Dimensions = dimensions }.Validate();

    [Fact]
    public void DefaultsAreValid()
    {
        var options = new MnemosynePostgreSqlOptions { Dimensions = 3 };
        Assert.Equal(16, options.HnswM);
        Assert.Equal(64, options.HnswEfConstruction);
        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void DimensionsOutOfRangeThrow(int dimensions)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MnemosynePostgreSqlOptions { Dimensions = dimensions }.Validate());
        Assert.Equal("Dimensions", ex.ParamName);
        Assert.Contains("2000", ex.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public void ValidHnswMPasses(int m) =>
        new MnemosynePostgreSqlOptions { Dimensions = 3, HnswM = m, HnswEfConstruction = 2 * m }.Validate();

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void HnswMOutOfRangeThrows(int m)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MnemosynePostgreSqlOptions { Dimensions = 3, HnswM = m }.Validate());
        Assert.Equal("HnswM", ex.ParamName);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1000)]
    public void ValidHnswEfConstructionPasses(int ef) =>
        new MnemosynePostgreSqlOptions { Dimensions = 3, HnswM = 2, HnswEfConstruction = ef }.Validate();

    // pgvector refuses an HNSW index whose ef_construction is below 2 * m (SQLSTATE 22023).
    [Theory]
    [InlineData(100, 199)]
    [InlineData(16, 31)]
    public void HnswEfConstructionBelowTwiceHnswMThrows(int m, int ef)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MnemosynePostgreSqlOptions { Dimensions = 3, HnswM = m, HnswEfConstruction = ef }.Validate());
        Assert.Equal("HnswEfConstruction", ex.ParamName);
        Assert.Contains("2 * HnswM", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 200)]
    [InlineData(16, 32)]
    public void HnswEfConstructionOfExactlyTwiceHnswMPasses(int m, int ef) =>
        new MnemosynePostgreSqlOptions { Dimensions = 3, HnswM = m, HnswEfConstruction = ef }.Validate();

    [Theory]
    [InlineData(3)]
    [InlineData(1001)]
    public void HnswEfConstructionOutOfRangeThrows(int ef)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MnemosynePostgreSqlOptions { Dimensions = 3, HnswEfConstruction = ef }.Validate());
        Assert.Equal("HnswEfConstruction", ex.ParamName);
    }
}
