using Mnemosyne.NET.PostgreSql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class PostgresVersionTests
{
    [Theory]
    [InlineData(150000, true)]
    [InlineData(150014, true)]
    [InlineData(170011, true)]
    [InlineData(180000, true)]
    [InlineData(149999, false)]
    [InlineData(140011, false)]
    [InlineData(90624, false)]
    public void ComparesTheServerVersionNumber(int serverVersionNum, bool supported) =>
        Assert.Equal(supported, PostgresVersion.IsSupported(serverVersionNum));
}
