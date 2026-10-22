using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class PostgresErrorsTests
{
    private static PostgresException Pg(string sqlState) => new("boom", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("23503")]
    [InlineData("55P03")]
    public void ConflictStates(string sqlState)
    {
        var error = PostgresErrors.Map(Pg(sqlState));
        Assert.NotNull(error);
        Assert.Equal(MnemosyneErrorCode.Conflict, error.Value.Code);
        Assert.Equal($"{sqlState}: boom", error.Value.Detail);
    }

    [Theory]
    [InlineData("08006")]
    [InlineData("57P01")]
    [InlineData("53100")]
    [InlineData("53200")]
    [InlineData("53300")]
    [InlineData("57014")]
    [InlineData("25006")]
    public void ConnectionAndResourceStatesAreUnavailable(string sqlState)
    {
        var error = PostgresErrors.Map(Pg(sqlState));
        Assert.NotNull(error);
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Value.Code);
    }

    [Fact]
    public void NpgsqlExceptionWithoutStateIsUnavailable()
    {
        var error = PostgresErrors.Map(new NpgsqlException("down"));
        Assert.NotNull(error);
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Value.Code);
        Assert.Equal("down", error.Value.Detail);
    }

    [Fact]
    public void WrappedTimeoutIsUnavailable() => AssertUnavailable(new NpgsqlException("timed out", new TimeoutException()));

    [Fact]
    public void WrappedIoErrorIsUnavailable() => AssertUnavailable(new NpgsqlException("io", new IOException()));

    private static void AssertUnavailable(Exception ex)
    {
        var error = PostgresErrors.Map(ex);
        Assert.NotNull(error);
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Value.Code);
    }

    [Theory]
    [InlineData("42P01")]
    [InlineData("23505")]
    [InlineData("57000")]
    [InlineData("25001")]
    public void OtherPostgresStateIsNull(string sqlState) => Assert.Null(PostgresErrors.Map(Pg(sqlState)));

    [Fact]
    public void OtherExceptionIsNull() => Assert.Null(PostgresErrors.Map(new InvalidOperationException()));
}
