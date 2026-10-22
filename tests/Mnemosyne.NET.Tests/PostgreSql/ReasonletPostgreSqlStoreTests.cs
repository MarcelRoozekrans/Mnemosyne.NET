using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.Tests.PostgreSql;

// The paths that need no database. The Docker tier, tests/Mnemosyne.NET.PostgreSql.Tests, covers the rest.
public sealed class ReasonletPostgreSqlStoreTests : IAsyncDisposable
{
    // Port 1 refuses the connection at once, so Npgsql fails without waiting for a timeout.
    private readonly NpgsqlDataSource _unreachable =
        new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=2;Pooling=false").Build();

    private static MnemosynePostgreSqlOptions Options => new() { Dimensions = 3 };

    [Fact]
    public void TheConstructorValidatesTheOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReasonletPostgreSqlStore(_unreachable, new MnemosynePostgreSqlOptions { Dimensions = 0 }));
    }

    [Fact]
    public void TheConstructorRefusesNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ReasonletPostgreSqlStore(null!, Options));
        Assert.Throws<ArgumentNullException>(() => new ReasonletPostgreSqlStore(_unreachable, null!));
    }

    [Fact]
    public async Task MigrateAgainstAnUnreachableServerIsUnavailable()
    {
        var result = await new ReasonletPostgreSqlStore(_unreachable, Options).MigrateAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task AMemberAgainstAnUnreachableServerIsUnavailable()
    {
        var result = await new ReasonletPostgreSqlStore(_unreachable, Options)
            .GetAsync(new ReasonletScope("s"), ReasonletId.New(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task CancellationThrowsRatherThanReturningAnError()
    {
        var store = new ReasonletPostgreSqlStore(_unreachable, Options);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.MigrateAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.GetAsync(new ReasonletScope("s"), ReasonletId.New(), cancelled.Token));
    }

    public ValueTask DisposeAsync() => _unreachable.DisposeAsync();
}
