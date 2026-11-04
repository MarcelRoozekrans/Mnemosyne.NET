using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
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

    /// <summary>A provider with the memory but without <c>AddReasonletCapture</c>.</summary>
    private static ServiceProvider Provider(FakeReasonletMemory memory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(memory);
        return services.BuildServiceProvider();
    }
}
