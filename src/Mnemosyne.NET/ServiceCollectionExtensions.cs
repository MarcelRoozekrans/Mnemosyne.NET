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
    /// <see cref="float"/>. An <see cref="IIntentExtractor"/> and an <see cref="IReasonletEvaluator"/> are
    /// optional and used when registered. Without an evaluator, the agent's own outcome counts for every run.
    /// The facade times <see cref="MnemosyneOptions.EvaluationTimeout"/> on the registered <see cref="TimeProvider"/>.
    /// The facade is a singleton that captures what it resolves, so register the store, the generator, the extractor and
    /// the evaluator as singletons. A scoped registration fails scope validation when the facade is resolved.
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
        // Both factories resolve the options rather than capture them, so a second AddMnemosyne, whose selector
        // registration TryAdd skips, cannot leave the selector and the facade with different options.
        services.TryAddSingleton<IReasonletSelector>(provider =>
            new ReasonletSelector(provider.GetRequiredService<MnemosyneOptions>().Ranking, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IReasonletMemory>(provider => new ReasonletMemory(
            provider.GetRequiredService<IReasonletStore>(),
            provider.GetRequiredService<IReasonletSelector>(),
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            provider.GetService<IIntentExtractor>(),
            provider.GetService<IReasonletEvaluator>(),
            provider.GetRequiredService<MnemosyneOptions>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Registers <see cref="ChatReasonletEvaluator"/> as the singleton <see cref="IReasonletEvaluator"/>, judging runs with the
    /// host's <see cref="IChatClient"/>.
    /// </summary>
    /// <remarks>
    /// The host registers the <see cref="IChatClient"/>, for example with a Microsoft.Extensions.AI provider package. When
    /// <see cref="ChatEvaluatorOptions.ChatClientKey"/> is set, the evaluator resolves the keyed client of that key
    /// instead of the unkeyed one. The evaluator is a singleton that captures the client it resolves, so register the client
    /// as a singleton. A scoped registration fails scope validation when the evaluator is resolved.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The settings, or null for the defaults.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static IServiceCollection AddMnemosyneChatEvaluator(this IServiceCollection services, ChatEvaluatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new ChatEvaluatorOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        services.AddSingleton<IReasonletEvaluator>(provider => new ChatReasonletEvaluator(
            options.ChatClientKey is { } key
                ? provider.GetRequiredKeyedService<IChatClient>(key)
                : provider.GetRequiredService<IChatClient>(),
            options));
        return services;
    }
}
