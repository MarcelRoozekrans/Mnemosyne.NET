using Mnemosyne.NET.PostgreSql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class LineageVersionsTests
{
    [Fact]
    public void InitialIsOneZero() => Assert.Equal("1.0", LineageVersions.Initial);

    [Theory]
    [InlineData("1.0", "1.1")]
    [InlineData("1.0,1.1,1.3", "1.4")]
    [InlineData("1.2", "1.3")]
    public void NextIsMaxMinorPlusOne(string versions, string expected) =>
        Assert.Equal(expected, LineageVersions.Next(versions.Split(',')));

    [Fact]
    public void MinorParses() => Assert.Equal(0, LineageVersions.Minor("1.0"));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("2.1")]
    [InlineData("1.x")]
    [InlineData("1.-1")]
    public void MalformedThrows(string version) =>
        Assert.Throws<FormatException>(() => LineageVersions.Minor(version));
}
