using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Registers the Reasonlet memory facade with a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IReasonletMemory"/> as a singleton <see cref="ReasonletMemory"/>, the options, the default
    /// <see cref="IReasonletSelector"/> and <see cref="TimeProvider.System"/>. The selector and the clock are added only
    /// when the host has not registered its own.
    /// </summary>
    /// <remarks>
    /// The host registers an <see cref="IReasonletStore"/>, for example with <c>AddMnemosynePostgreSql</c>, and an
    /// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> of <see cref="string"/> to <see cref="Embedding{T}"/> of
    /// <see cref="float"/>. An <see cref="IIntentExtractor"/> is optional and used when registered.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">
    /// The settings, or null for the defaults. They are passed as an object rather than a configure delegate because
    /// their members are init-only.
    /// </param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static IServiceCollection AddMnemosyne(this IServiceCollection services, MnemosyneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new MnemosyneOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IReasonletSelector>(provider =>
            new ReasonletSelector(options.Ranking, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IReasonletMemory>(provider => new ReasonletMemory(
            provider.GetRequiredService<IReasonletStore>(),
            provider.GetRequiredService<IReasonletSelector>(),
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            provider.GetService<IIntentExtractor>(),
            options));
        return services;
    }
}
