using AI.Sentinel;
using AI.Sentinel.Detectors.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Sentinel;
using Mnemosyne.NET.Tests.Abstractions;
using Mnemosyne.NET.Tests.Core;
using Mnemosyne.Samples;
using Xunit;

namespace Mnemosyne.NET.Tests.Sentinel;

public sealed class SentinelRegistrationTests
{
    // The fixture of AI.Sentinel's own SEC-02 span tests: an unquoted api_key assignment.
    private const string CredentialQuestion = "api_key=abc123def456ghi789";

    private readonly FakeStore _store = new() { Dimensions = HashingEmbeddingGenerator.Dimensions };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
        return services;
    }

    [Fact]
    public void RegistersGuardOnDefaultPipeline()
    {
        var services = Services();
        services.AddAISentinel();
        services.AddMnemosyneSentinel();
        using var provider = services.BuildServiceProvider();

        var guard = provider.GetRequiredService<ICaptureGuard>();

        Assert.IsType<SentinelCaptureGuard>(guard);
        Assert.Same(guard, provider.GetRequiredService<ICaptureGuard>());
    }

    [Fact]
    public async Task NamedPipelineIsResolvedByKey()
    {
        // AI.Sentinel 4.1.1 only supports named pipelines next to the default one: the shared detector pool is built
        // from the default pipeline's options. So the default pipeline switches SEC-02 off, and the named one keeps it.
        // Only the keyed pipeline can then block the credential.
        var services = Services();
        services.AddAISentinel(o => o.Configure<CredentialExposureDetector>(c => c.Enabled = false));
        services.AddAISentinel("capture", _ => { });
        services.AddMnemosyneSentinel(new SentinelCaptureGuardOptions { PipelineName = "capture" });
        services.AddMnemosyne();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>()
            .RememberAsync(TestData.Capture() with { Question = CredentialQuestion }, Ct);

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal("ai-sentinel/SEC-02", result.Error.Detail);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task TheDefaultPipelineIsNotUsedWhenANameIsSet()
    {
        // The reverse of the named test: with SEC-02 off in the named pipeline, the unkeyed one's block must not happen.
        var services = Services();
        services.AddAISentinel();
        services.AddAISentinel("capture", o => o.Configure<CredentialExposureDetector>(c => c.Enabled = false));
        services.AddMnemosyneSentinel(new SentinelCaptureGuardOptions { PipelineName = "capture" });
        services.AddMnemosyne();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>()
            .RememberAsync(TestData.Capture() with { Question = CredentialQuestion }, Ct);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
        Assert.Equal(1, _store.Calls);
    }

    [Fact]
    public void MissingPipelineFailsAtResolution()
    {
        var services = Services();
        services.AddMnemosyneSentinel();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICaptureGuard>());
    }

    [Fact]
    public void InvalidOptionsThrowArgumentException()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMnemosyneSentinel(new SentinelCaptureGuardOptions { GuardName = " " }));

        Assert.Equal("options", error.ParamName);
    }

    [Fact]
    public void NullServicesThrows() =>
        Assert.Throws<ArgumentNullException>("services", () => global::Mnemosyne.NET.Sentinel.ServiceCollectionExtensions.AddMnemosyneSentinel(null!));

    [Fact]
    public async Task AddMnemosynePicksUpTheGuard()
    {
        var services = Services();
        services.AddAISentinel();
        services.AddMnemosyneSentinel();
        services.AddMnemosyne();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IReasonletMemory>()
            .RememberAsync(TestData.Capture() with { Question = CredentialQuestion }, Ct);

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
    }
}
