using AI.Sentinel;
using AI.Sentinel.Detection;
using AI.Sentinel.Domain;
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

    private ServiceProvider Provider(SentinelCaptureGuardOptions? guardOptions = null, Action<SentinelOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletStore>(_store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
        services.AddAISentinel(configure);
        services.AddMnemosyneSentinel(guardOptions);
        services.AddMnemosyne();
        return services.BuildServiceProvider();
    }

    private async Task<ZeroAlloc.Results.Result<ReasonletReceipt, MnemosyneError>> Remember(
        string question, SentinelCaptureGuardOptions? guardOptions = null, Action<SentinelOptions>? configure = null)
    {
        using var provider = Provider(guardOptions, configure);
        return await provider.GetRequiredService<IReasonletMemory>()
            .RememberAsync(TestData.Capture() with { Question = question }, Ct);
    }

    [Fact]
    public async Task CredentialInQuestionIsBlocked()
    {
        var result = await Remember("Why does api_key=abc123def456ghi789 fail?"); // gitleaks:allow - fake credential fixture

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

    // AI.Sentinel's PiiLeakageDetector rates an email address Medium, below the default High threshold. The fixture is the
    // one of its own span tests.
    [Fact]
    public async Task EmailInQuestionIsStoredAtTheDefaultThreshold()
    {
        var result = await Remember("Mail jan@acme.nl about invoice 42.");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
        Assert.Equal(1, _store.Calls);
    }

    [Fact]
    public async Task EmailInQuestionIsBlockedAtMedium()
    {
        var result = await Remember(
            "Mail jan@acme.nl about invoice 42.", new SentinelCaptureGuardOptions { BlockAtSeverity = Severity.Medium });

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal("ai-sentinel/SEC-23", result.Error.Detail);
        Assert.Equal(0, _store.Calls);
    }

    // AI.Sentinel 5.1.1 reports a detector that threw through PipelineResult.Failures and IsComplete. A scan that did not
    // finish proves nothing, so with no blocking detection the guard fails closed as Unavailable. The detail holds the count
    // and never the detector's message, which can quote the capture.
    [Fact]
    public async Task AThrowingDetectorFailsTheCaptureClosedAsUnavailable()
    {
        var failures = new List<string>();
        var result = await Remember(
            "Where is invoice 42?",
            configure: o =>
            {
                o.AddDetector<ThrowingDetector>();
                o.OnDetectorFailure = failures.Add;
            });

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal(0, _store.Calls);
        Assert.Single(failures);
        Assert.Equal("The capture guard failed.", result.Error.Message);
        Assert.Null(result.Error.Detail);
    }

    [Fact]
    public async Task ABlockingDetectionWinsOverAFailedDetector()
    {
        var failures = new List<string>();
        var result = await Remember(
            "Why does api_key=abc123def456ghi789 fail?", // gitleaks:allow - fake credential fixture
            configure: o =>
            {
                o.AddDetector<ThrowingDetector>();
                o.OnDetectorFailure = failures.Add;
            });

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal("ai-sentinel/SEC-02", result.Error.Detail);
        Assert.Equal(0, _store.Calls);
        Assert.Single(failures);
    }

    [Fact]
    public async Task ACompleteCleanScanIsStillAllowed()
    {
        var failures = new List<string>();
        var result = await Remember("Where is invoice 42?", configure: o => o.OnDetectorFailure = failures.Add);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
        Assert.Equal(1, _store.Calls);
        Assert.Empty(failures);
    }

    private sealed class ThrowingDetector : IDetector
    {
        private static readonly DetectorId _id = new("TEST-THROW");

        public DetectorId Id => _id;

        public DetectorCategory Category => DetectorCategory.Operational;

        public ValueTask<DetectionResult> AnalyzeAsync(SentinelContext ctx, CancellationToken ct) =>
            throw new HttpRequestException("The detector's endpoint is down.");
    }
}
