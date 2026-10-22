using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Npgsql;

namespace Mnemosyne.NET.PostgreSql;

/// <summary>Registers the PostgreSQL Reasonlet store with a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ReasonletPostgreSqlStore"/> as a singleton, and <see cref="IReasonletStore"/> as that same
    /// instance. The store takes the <see cref="NpgsqlDataSource"/> from the container, so the host registers one.
    /// The host also runs <see cref="ReasonletPostgreSqlStore.MigrateAsync"/> at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">
    /// The store settings. They are passed as an object rather than a configure delegate because their members are
    /// <c>required</c> init members, which a delegate cannot build.
    /// </param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public static IServiceCollection AddMnemosynePostgreSql(this IServiceCollection services, MnemosynePostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(provider => new ReasonletPostgreSqlStore(provider.GetRequiredService<NpgsqlDataSource>(), options));
        services.AddSingleton<IReasonletStore>(provider => provider.GetRequiredService<ReasonletPostgreSqlStore>());
        return services;
    }
}
