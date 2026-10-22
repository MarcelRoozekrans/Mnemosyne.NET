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
    public void ConnectionStatesAreUnavailable(string sqlState)
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

    [Fact]
    public void OtherPostgresStateIsNull() => Assert.Null(PostgresErrors.Map(Pg("42P01")));

    [Fact]
    public void OtherExceptionIsNull() => Assert.Null(PostgresErrors.Map(new InvalidOperationException()));
}
