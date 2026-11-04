using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>What <c>AddReasonletCapture</c> registers, and that stopping the host or disposing the provider drains the queue.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class CaptureRegistrationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddReasonletCaptureRegistersQueueAndOneHostedService()
    {
        var options = new ReasonletCaptureOptions { Source = "custom" };
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(CaptureTestData.Memory());
        services.AddReasonletCapture();
        services.AddReasonletCapture(options);
        await using var provider = services.BuildServiceProvider();

        var hosted = Assert.Single(provider.GetServices<IHostedService>());

        Assert.IsType<ReasonletCaptureHostedService>(hosted);
        Assert.Same(options, provider.GetRequiredService<ReasonletCaptureOptions>());
        Assert.Same(provider.GetRequiredService<ReasonletCaptureQueue>(), provider.GetRequiredService<ReasonletCaptureQueue>());
        Assert.Same(provider.GetRequiredService<ReasonletCaptureProcessor>(), provider.GetRequiredService<ReasonletCaptureProcessor>());
    }

    [Fact]
    public void NullOptionsRegisterTheDefaults()
    {
        var services = new ServiceCollection();
        services.AddReasonletCapture(null);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(256, provider.GetRequiredService<ReasonletCaptureOptions>().Capacity);
    }

    [Fact]
    public void InvalidOptionsThrow()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddReasonletCapture(new ReasonletCaptureOptions { Capacity = 0 }));

        Assert.Equal("options", error.ParamName);
    }

    [Fact]
    public void ANullServiceCollectionThrows() =>
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddReasonletCapture(null!));

    [Fact]
    public async Task HostStopDrainsTheQueue()
    {
        var memory = CaptureTestData.Memory();
        await using var provider = Provider(memory);
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
        {
            await service.StartAsync(Ct);
        }

        Assert.True(provider.GetRequiredService<ReasonletCaptureQueue>().TryEnqueue(CaptureTestData.Item()));
        foreach (var service in hosted)
        {
            await service.StopAsync(Ct).WaitAsync(Guard, Ct);
        }

        Assert.Equal(CaptureTestData.Question, Assert.Single(memory.Captures).Question);
    }

    [Fact]
    public async Task DisposingTheProviderDrainsTheQueue()
    {
        var memory = CaptureTestData.Memory();
        var provider = Provider(memory);

        Assert.True(provider.GetRequiredService<ReasonletCaptureQueue>().TryEnqueue(CaptureTestData.Item()));
        await provider.DisposeAsync().AsTask().WaitAsync(Guard, Ct);

        Assert.Equal(CaptureTestData.Question, Assert.Single(memory.Captures).Question);
    }

    [Fact]
    public async Task DisposingTheProviderSynchronouslyDrainsTheQueue()
    {
        var memory = CaptureTestData.Memory();
        var provider = Provider(memory);

        Assert.True(provider.GetRequiredService<ReasonletCaptureQueue>().TryEnqueue(CaptureTestData.Item()));
        await Task.Run(provider.Dispose, Ct).WaitAsync(Guard, Ct);

        Assert.Equal(CaptureTestData.Question, Assert.Single(memory.Captures).Question);
    }

    private static ServiceProvider Provider(FakeReasonletMemory memory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(memory);
        services.AddReasonletCapture();
        return services.BuildServiceProvider();
    }
}
