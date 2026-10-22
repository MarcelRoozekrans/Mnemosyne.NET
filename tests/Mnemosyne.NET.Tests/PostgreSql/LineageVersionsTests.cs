using Mnemosyne.NET.PostgreSql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class LineageVersionsTests
{
    [Fact]
    public void InitialIsOneZero() => Assert.Equal("1.0", LineageVersions.Initial);

    [Theory]
    [InlineData(0L, "1.1")]
    [InlineData(3L, "1.4")]
    [InlineData(2147483646L, "1.2147483647")]
    public void NextIsMaxMinorPlusOne(long maxMinor, string expected) =>
        Assert.Equal(expected, LineageVersions.Next(maxMinor, null));

    [Fact]
    public void NextOfALineageWithoutAWellFormedVersionIsOneOne() =>
        // Only reachable for a lineage with no rows, which a locked top rules out. A new Reasonlet uses Initial.
        Assert.Equal("1.1", LineageVersions.Next(null, null));

    [Theory]
    [InlineData(2147483647L)]
    [InlineData(99999999999L)]
    public void NextThrowsRatherThanOverflow(long maxMinor) =>
        Assert.Throws<OverflowException>(() => LineageVersions.Next(maxMinor, null));

    [Fact]
    public void AMalformedVersionInTheLineageIsABugAndThrows()
    {
        var thrown = Assert.Throws<FormatException>(() => LineageVersions.Next(3, "1.01"));

        Assert.Equal("'1.01' is not a lineage version of the form 1.k.", thrown.Message);
    }
}
