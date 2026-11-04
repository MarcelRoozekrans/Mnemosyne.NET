using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>What <c>UseReasonlets</c> checks and resolves when the pipeline is built.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class UseReasonletsTests
{
    private static readonly ReasonletScope Scope = new("support");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BuildWithoutQueueThrowsNamingAddReasonletCapture()
    {
        using var provider = Provider(new FakeReasonletMemory());
        var builder = new ChatClientBuilder(FakeChatClient.Replying("done")).UseReasonlets();

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(provider));

        Assert.Equal(
            "UseReasonlets captures by default and needs services.AddReasonletCapture(); call it, or set ReasonletChatOptions.Capture = false.",
            error.Message);
    }

    [Fact]
    public async Task BuildWithoutQueueSucceedsWhenCaptureIsOff()
    {
        var memory = new FakeReasonletMemory();
        using var provider = Provider(memory);
        var inner = FakeChatClient.Replying("done");

        using var client = new ChatClientBuilder(inner)
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope, Capture = false })
            .Build(provider);
        var response = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal("done", response.Text);
        Assert.NotNull(client.GetService<ReasonletChatClient>());
        Assert.Equal(Scope, Assert.Single(memory.Queries).Scope);
    }

    [Fact]
    public void InvalidOptionsThrow()
    {
        var builder = new ChatClientBuilder(FakeChatClient.Replying("done"));

        var error = Assert.Throws<ArgumentException>(() =>
            builder.UseReasonlets(new ReasonletChatOptions { RecallTimeout = TimeSpan.Zero }));

        Assert.Equal("options", error.ParamName);
        Assert.Contains(nameof(ReasonletChatOptions.RecallTimeout), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullBuilderThrows() =>
        Assert.Throws<ArgumentNullException>(() => ReasonletChatClientBuilderExtensions.UseReasonlets(null!));

    [Fact]
    public async Task UsesRegisteredTimeProvider()
    {
        var step = TimeSpan.FromMilliseconds(777);
        var store = CaptureTestData.Memory();
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(store);
        services.AddSingleton<TimeProvider>(new SteppingTimeProvider(step));
        services.AddReasonletCapture();
        await using var provider = services.BuildServiceProvider();
        var inner = FakeChatClient.Replying(new ChatResponse([.. CaptureTestData.Refund().Skip(1)]));

        using var client = new ChatClientBuilder(inner)
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope })
            .Build(provider);
        await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);
        await provider.GetRequiredService<ReasonletCaptureQueue>().DrainAsync(Ct);

        Assert.Equal(step, Assert.Single(store.Captures).Outcome.Duration);
    }

    [Fact]
    public void BuildWithoutMemoryThrowsNamingAddMnemosyne()
    {
        var services = new ServiceCollection();
        services.AddReasonletCapture();
        using var provider = services.BuildServiceProvider();
        var builder = new ChatClientBuilder(FakeChatClient.Replying("done")).UseReasonlets();

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(provider));

        Assert.Equal("UseReasonlets needs an IReasonletMemory; register one, for example with services.AddMnemosyne().", error.Message);
    }

    [Fact]
    public async Task TheMemoryAndTheQueueAreResolvedOnFirstUseOnly()
    {
        var resolved = new List<string>();
        var memory = CaptureTestData.Memory();
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ =>
        {
            resolved.Add("memory");
            return memory;
        });
        services.AddReasonletCapture();
        await using var provider = services.BuildServiceProvider();

        using var client = new ChatClientBuilder(FakeChatClient.Replying(new ChatResponse([.. CaptureTestData.Refund().Skip(1)])))
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope })
            .Build(provider);
        Assert.Empty(resolved);

        await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);
        await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);
        await provider.GetRequiredService<ReasonletCaptureQueue>().DrainAsync(Ct);

        Assert.Equal(["memory"], resolved);
        Assert.Equal(2, memory.Queries.Count);
        Assert.Equal(2, memory.Captures.Count);
    }

    [Fact]
    public async Task WithoutIsServiceTheQueueIsResolvedOnFirstUseOnly()
    {
        var memory = CaptureTestData.Memory();
        var queue = new ReasonletCaptureQueue(new ReasonletCaptureProcessor(memory, new ReasonletCaptureOptions()), new ReasonletCaptureOptions());
        await using var _ = queue;
        var provider = new MinimalProvider { [typeof(IReasonletMemory)] = () => memory, [typeof(ReasonletCaptureQueue)] = () => queue };

        using var client = new ChatClientBuilder(FakeChatClient.Replying(new ChatResponse([.. CaptureTestData.Refund().Skip(1)])))
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope })
            .Build(provider);
        Assert.Equal(0, provider.Resolutions(typeof(ReasonletCaptureQueue)));
        Assert.Equal(0, provider.Resolutions(typeof(IReasonletMemory)));

        await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);
        await queue.DrainAsync(Ct);

        Assert.Equal(1, provider.Resolutions(typeof(ReasonletCaptureQueue)));
        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task WithoutIsServiceAMissingQueueIsCountedOnTheCall()
    {
        var provider = new MinimalProvider { [typeof(IReasonletMemory)] = () => new FakeReasonletMemory() };
        using var client = new ChatClientBuilder(FakeChatClient.Replying("done"))
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope })
            .Build(provider);
        using var telemetry = new TelemetryCapture();

        var response = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal("done", response.Text);
        AssertResolutionFailures(telemetry, "mnemosyne.capture.queue", 1);
    }

    [Fact]
    public async Task AThrowingMemoryFactoryWithRecallOnLeavesTheCallUnbroken()
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ =>
        {
            resolutions++;
            throw new InvalidOperationException("broken");
        });
        await using var provider = services.BuildServiceProvider();
        var inner = FakeChatClient.Replying("done");
        using var client = new ChatClientBuilder(inner)
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope, Capture = false })
            .Build(provider);
        using var telemetry = new TelemetryCapture();

        var first = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);
        var second = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal("done", first.Text);
        Assert.Equal("done", second.Text);
        Assert.All(inner.Requests, request => Assert.Equal(ChatRole.User, Assert.Single(request).Role));

        // The failure is not cached: each call tries again, and each failure counts once.
        Assert.Equal(2, resolutions);
        AssertResolutionFailures(telemetry, "mnemosyne.chat.recalls", 2);
    }

    [Fact]
    public async Task AThrowingMemoryFactoryWithTheToolOnLeavesTheToolOut()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ => throw new InvalidOperationException("broken"));
        await using var provider = services.BuildServiceProvider();
        var inner = FakeChatClient.Replying("done");
        using var client = new ChatClientBuilder(inner)
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope, Recall = false, Capture = false, ExposeFindProcedure = true })
            .Build(provider);
        using var telemetry = new TelemetryCapture();

        var response = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal("done", response.Text);
        Assert.Null(Assert.Single(inner.Options));
        AssertResolutionFailures(telemetry, "mnemosyne.chat.recalls", 1);
    }

    [Fact]
    public async Task AThrowingQueueFactoryLeavesTheCallUnbroken()
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(new FakeReasonletMemory());
        services.AddSingleton<ReasonletCaptureQueue>(_ =>
        {
            resolutions++;
            throw new InvalidOperationException("broken");
        });
        await using var provider = services.BuildServiceProvider();
        var inner = FakeChatClient.Replying("done");
        using var client = new ChatClientBuilder(inner)
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope })
            .Build(provider);
        using var telemetry = new TelemetryCapture();

        var first = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);
        var second = await client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal("done", first.Text);
        Assert.Equal("done", second.Text);
        Assert.Equal(2, resolutions);
        AssertResolutionFailures(telemetry, "mnemosyne.capture.queue", 2);
    }

    [Fact]
    public async Task TheCallersCancellationDuringMemoryResolutionPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ => throw new OperationCanceledException(cts.Token));
        await using var provider = services.BuildServiceProvider();
        using var client = new ChatClientBuilder(FakeChatClient.Replying("done"))
            .UseReasonlets(new ReasonletChatOptions { DefaultScope = Scope, Capture = false })
            .Build(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new(ChatRole.User, CaptureTestData.Question)], null, cts.Token));
    }

    private static void AssertResolutionFailures(TelemetryCapture telemetry, string instrument, int count)
    {
        var failures = telemetry.Of(instrument);
        Assert.Equal(count, failures.Count);
        Assert.All(failures, failure =>
        {
            Assert.Equal("exception", failure.Tag("mnemosyne.outcome"));
            Assert.Equal(typeof(InvalidOperationException).FullName, failure.Tag("error.type"));
        });
    }

    /// <summary>
    /// A minimal container: a factory per service type and a count of how often each was resolved. It offers no
    /// <see cref="IServiceProviderIsService"/>, so it cannot say whether a service is registered.
    /// </summary>
    private sealed class MinimalProvider : IServiceProvider
    {
        private readonly Dictionary<Type, Func<object>> _factories = [];
        private readonly Dictionary<Type, int> _resolutions = [];

        public Func<object> this[Type type]
        {
            set => _factories[type] = value;
        }

        public int Resolutions(Type type) => _resolutions.GetValueOrDefault(type);

        public object? GetService(Type serviceType)
        {
            if (!_factories.TryGetValue(serviceType, out var factory))
            {
                return null;
            }

            _resolutions[serviceType] = Resolutions(serviceType) + 1;
            return factory();
        }
    }

    /// <summary>A provider with the memory but without <c>AddReasonletCapture</c>.</summary>
    private static ServiceProvider Provider(FakeReasonletMemory memory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(memory);
        return services.BuildServiceProvider();
    }
}
