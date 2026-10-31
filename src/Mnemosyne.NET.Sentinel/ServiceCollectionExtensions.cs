using AI.Sentinel;
using AI.Sentinel.Detection;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Sentinel;

/// <summary>Registers the AI.Sentinel capture guard with a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SentinelCaptureGuard"/> as the singleton <see cref="ICaptureGuard"/>, running the host's
    /// AI.Sentinel detection pipeline over each capture before it is stored.
    /// </summary>
    /// <remarks>
    /// The host registers AI.Sentinel itself with <c>AddAISentinel</c>, before or after this call. The guard resolves the
    /// <see cref="IDetectionPipeline"/> and <see cref="SentinelOptions"/> when the guard is first resolved, so a missing
    /// pipeline fails then and not at registration. When <see cref="SentinelCaptureGuardOptions.PipelineName"/> is set, the
    /// guard resolves the keyed pipeline and options of that name, as registered by the named
    /// <c>AddAISentinel</c> overload, instead of the unkeyed ones. The guard is a singleton that captures what it resolves.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The settings, or null for the defaults.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static IServiceCollection AddMnemosyneSentinel(this IServiceCollection services, SentinelCaptureGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new SentinelCaptureGuardOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        services.AddSingleton<ICaptureGuard>(provider => options.PipelineName is { } name
            ? new SentinelCaptureGuard(
                provider.GetRequiredKeyedService<IDetectionPipeline>(name),
                provider.GetRequiredKeyedService<SentinelOptions>(name),
                options)
            : new SentinelCaptureGuard(
                provider.GetRequiredService<IDetectionPipeline>(),
                provider.GetRequiredService<SentinelOptions>(),
                options));
        return services;
    }
}
