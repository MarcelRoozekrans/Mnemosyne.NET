using Mnemosyne.NET.PostgreSql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class PgVectorVersionTests
{
    [Theory]
    [InlineData("0.8.0", true)]
    [InlineData("0.8.1", true)]
    [InlineData("0.10.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.7.4", false)]
    [InlineData("0.7.99", false)]
    [InlineData("0.2.0", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ComparesVersionsNumerically(string? found, bool supported) =>
        Assert.Equal(supported, PgVectorVersion.IsSupported(found));
}
