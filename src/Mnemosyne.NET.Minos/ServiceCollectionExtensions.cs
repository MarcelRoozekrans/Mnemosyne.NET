using Microsoft.Extensions.DependencyInjection;
using Minos;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Minos;

/// <summary>Registers the Minos evaluator with a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MinosReasonletEvaluator"/> as the singleton <see cref="IReasonletEvaluator"/>, judging runs with the
    /// host's <see cref="IDecisionClient"/>.
    /// </summary>
    /// <remarks>
    /// The host registers the <see cref="IDecisionClient"/>, for example with a Minos.NET provider package. When
    /// <see cref="MinosEvaluatorOptions.DecisionClientKey"/> is set, the evaluator resolves the keyed client of that key
    /// instead of the unkeyed one. The evaluator is a singleton that captures the client it resolves, so register the client
    /// as a singleton. A scoped client would be captured by the singleton, and fails scope validation where that is enabled.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The settings, or null for the defaults.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static IServiceCollection AddMnemosyneMinosEvaluator(this IServiceCollection services, MinosEvaluatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new MinosEvaluatorOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        services.AddSingleton<IReasonletEvaluator>(provider => new MinosReasonletEvaluator(
            options.DecisionClientKey is { } key
                ? provider.GetRequiredKeyedService<IDecisionClient>(key)
                : provider.GetRequiredService<IDecisionClient>(),
            options));
        return services;
    }
}
