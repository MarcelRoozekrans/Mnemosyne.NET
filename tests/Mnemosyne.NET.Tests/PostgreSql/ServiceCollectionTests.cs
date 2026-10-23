using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;
using Xunit;
using PostgreSqlRegistration = Mnemosyne.NET.PostgreSql.ServiceCollectionExtensions;

namespace Mnemosyne.NET.Tests.PostgreSql;

public sealed class ServiceCollectionTests : IAsyncDisposable
{
    // Registered as an instance, so the container does not own it and the test disposes it.
    private readonly NpgsqlDataSource _dataSource =
        new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d").Build();

    private ServiceProvider Provider(MnemosynePostgreSqlOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_dataSource);
        services.AddMnemosynePostgreSql(options);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheStoreResolvesThroughThePortAndIsOneSingleton()
    {
        using var provider = Provider(new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var port = provider.GetRequiredService<IReasonletStore>();

        Assert.IsType<ReasonletPostgreSqlStore>(port);
        Assert.Same(port, provider.GetRequiredService<IReasonletStore>());
        Assert.Same(port, provider.GetRequiredService<ReasonletPostgreSqlStore>());
    }

    [Fact]
    public void InvalidOptionsThrowAtRegistration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 0 }));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => PostgreSqlRegistration.AddMnemosynePostgreSql(null!, new MnemosynePostgreSqlOptions { Dimensions = 3 }));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddMnemosynePostgreSql(null!));
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
