using AI.Sentinel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Sentinel;
using Mnemosyne.NET.Tests.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Mnemosyne.Samples;
using Xunit;

namespace Mnemosyne.NET.Tests.Sentinel;

/// <summary>The facade with the real AI.Sentinel pipeline behind the capture guard, from the container down to the store.</summary>
public sealed class SentinelFacadeTests
{
    private readonly FakeStore _store = new() { Dimensions = HashingEmbeddingGenerator.Dimensions };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
        services.AddAISentinel();
        services.AddMnemosyneSentinel();
        services.AddMnemosyne();
        return services.BuildServiceProvider();
    }

    private async Task<ZeroAlloc.Results.Result<ReasonletReceipt, MnemosyneError>> Remember(string question)
    {
        using var provider = Provider();
        return await provider.GetRequiredService<IReasonletMemory>()
            .RememberAsync(TestData.Capture() with { Question = question }, Ct);
    }

    [Fact]
    public async Task CredentialInQuestionIsBlocked()
    {
        var result = await Remember("Why does api_key=abc123def456ghi789 fail?");

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal("ai-sentinel/SEC-02", result.Error.Detail);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task IbanInQuestionIsBlocked()
    {
        var result = await Remember("Which account is NL91ABNA0417164300?");

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal("ai-sentinel/SEC-23", result.Error.Detail);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task HarmlessCaptureIsStored()
    {
        var result = await Remember("Where is invoice 42?");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
        Assert.Equal(1, _store.Calls);
    }
}
