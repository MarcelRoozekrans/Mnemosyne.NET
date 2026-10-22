using Mnemosyne.NET.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// One pgvector container per test class, shared by its tests through <see cref="IClassFixture{TFixture}"/>.
/// Tests isolate themselves with <see cref="NewScope"/>, or with <see cref="CreateEmptyDatabaseAsync"/>
/// when they need a database whose schema no other test has touched.
/// </summary>
public sealed class PgVectorFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
    private readonly List<NpgsqlDataSource> _emptyDatabases = [];

    /// <summary>The data source of the container's default database.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
    }

    public ReasonletPostgreSqlStore CreateStore(int dimensions = 3) =>
        new(DataSource, new MnemosynePostgreSqlOptions { Dimensions = dimensions });

    public static ReasonletScope NewScope() => new($"scope-{Guid.NewGuid():N}");

    /// <summary>Creates a new, empty database in the container and returns a data source for it.</summary>
    public async Task<NpgsqlDataSource> CreateEmptyDatabaseAsync()
    {
        var name = $"db_{Guid.NewGuid():N}";
        await using (var create = DataSource.CreateCommand($"CREATE DATABASE {name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        var dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        _emptyDatabases.Add(dataSource);
        return dataSource;
    }

    public async ValueTask DisposeAsync()
    {
        // The container goes in a finally: a data source that throws on disposal must not leave it running.
        try
        {
            foreach (var dataSource in _emptyDatabases)
            {
                await dataSource.DisposeAsync();
            }

            if (DataSource is not null)
            {
                await DataSource.DisposeAsync();
            }
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }
}
