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
    /// <see cref="float"/>. An <see cref="IIntentExtractor"/>, an <see cref="IReasonletEvaluator"/> and an
    /// <see cref="ICaptureGuard"/> are optional and used when registered. Without an evaluator, the agent's own outcome
    /// counts for every run. Without a guard, every capture is remembered unchecked.
    /// The facade times <see cref="MnemosyneOptions.EvaluationTimeout"/> and <see cref="MnemosyneOptions.GuardTimeout"/>
    /// on the registered <see cref="TimeProvider"/>.
    /// The facade is a singleton that captures what it resolves, so register the store, the generator, the extractor, the
    /// evaluator and the guard as singletons. A scoped registration fails scope validation when the facade is resolved.
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
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ICaptureGuard>()));
        return services;
    }

    /// <summary>
    /// Wraps the registered <see cref="IReasonletSelector"/> in a <see cref="VariantSelector"/>, so a recall returns one
    /// version per lineage.
    /// </summary>
    /// <remarks>
    /// Call it after <see cref="AddMnemosyne"/>, and after a host registers its own selector: it wraps whatever
    /// unkeyed selector is registered last, so a host's own selector is grouped too. The wrapper keeps the lifetime of the
    /// selector it wraps. A second call changes nothing. The wrapped selector receives a query whose
    /// <see cref="ReasonletQuery.Top"/> equals the candidate count, so it can exceed the query's limit of 50 when
    /// <see cref="MnemosyneOptions.CandidatePool"/> does.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="IReasonletSelector"/> is registered yet.</exception>
    public static IServiceCollection AddMnemosyneVariantSelection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(VariantSelectionMarker)))
        {
            return services;
        }

        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IReasonletSelector) && !services[i].IsKeyedService)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException(
                "No IReasonletSelector is registered. Call AddMnemosyne, or register a selector, before AddMnemosyneVariantSelection.");
        }

        // The original moves to a keyed registration, so the container still builds and disposes it.
        var original = services[index];
        var key = VariantSelectionMarker.OriginalKey;
        ServiceDescriptor keyed;
        if (original.ImplementationInstance is { } instance)
        {
            keyed = new ServiceDescriptor(typeof(IReasonletSelector), key, instance);
        }
        else if (original.ImplementationFactory is { } factory)
        {
            keyed = new ServiceDescriptor(typeof(IReasonletSelector), key, (provider, _) => factory(provider), original.Lifetime);
        }
        else
        {
            keyed = new ServiceDescriptor(typeof(IReasonletSelector), key, original.ImplementationType!, original.Lifetime);
        }

        services[index] = ServiceDescriptor.Describe(
            typeof(IReasonletSelector),
            provider => new VariantSelector(provider.GetRequiredKeyedService<IReasonletSelector>(key)),
            original.Lifetime);
        services.Add(keyed);
        services.AddSingleton<VariantSelectionMarker>();
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
    /// as a singleton. A scoped client would be captured by the singleton, and fails scope validation where that is enabled.
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

    /// <summary>
    /// Registers the background capture that <c>UseReasonlets</c> hands finished chat calls to: a bounded queue with one
    /// reader, and a hosted service that drains it when the host stops.
    /// </summary>
    /// <remarks>
    /// The capture remembers through the registered <see cref="IReasonletMemory"/>, for example from
    /// <see cref="AddMnemosyne"/>. The queue's reader starts on the first capture, so a host without the generic host
    /// still captures. Without a host, disposing the service provider, synchronously or asynchronously, drains the queue.
    /// After <see cref="ReasonletCaptureOptions.DrainTimeout"/> the drain cancels the work that is left, then waits for the
    /// memory to honour that cancellation. A second call registers no second queue, processor or hosted service, and its
    /// options replace the first call's, as with <see cref="AddMnemosyne"/>.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The settings, or null for the defaults.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static IServiceCollection AddReasonletCapture(this IServiceCollection services, ReasonletCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new ReasonletCaptureOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        // The last options registered win; both factories resolve them rather than capture them.
        services.AddSingleton(options);
        services.TryAddSingleton(provider => new ReasonletCaptureProcessor(
            provider.GetRequiredService<IReasonletMemory>()));
        services.TryAddSingleton(provider => new ReasonletCaptureQueue(
            provider.GetRequiredService<ReasonletCaptureProcessor>(),
            provider.GetRequiredService<ReasonletCaptureOptions>()));
        services.TryAddSingleton<IReasonletCaptureSink>(provider => provider.GetRequiredService<ReasonletCaptureQueue>());
        services.AddHostedService<ReasonletCaptureHostedService>();
        return services;
    }
}
