using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Chat;
using Mnemosyne.NET.Tests.Core;
using Mnemosyne.Samples;
using Xunit;
using Result = ZeroAlloc.Results.Result<Mnemosyne.NET.Abstractions.ReasonletReceipt, Mnemosyne.NET.Abstractions.MnemosyneError>;

namespace Mnemosyne.NET.Tests.Samples;

/// <summary>The sample's completion signal: the outcome of each finished background capture, read from the run counter.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, so the watcher hears only its own captures.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class CaptureRunWatcherTests
{
    /// <summary>A hang guard only: a capture of the fake memory finishes in milliseconds.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Provider(FakeReasonletMemory memory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(memory);
        services.AddReasonletCapture();
        return services.BuildServiceProvider();
    }

    private static bool Capture(ServiceProvider provider) =>
        provider.GetRequiredService<IReasonletCaptureSink>()
            .TryCapture(new ReasonletCaptureRun(new ReasonletScope("scope"), CaptureTestData.Refund()));

    [Fact]
    public async Task ReportsTheOutcomeOfAFinishedCapture()
    {
        using var watcher = new CaptureRunWatcher();
        await using var provider = Provider(CaptureTestData.Memory());

        Assert.True(Capture(provider));

        Assert.Equal("new_reasonlet", await watcher.NextOutcomeAsync(Guard, Ct));
    }

    [Fact]
    public async Task ReportsABlockedCaptureByItsErrorName()
    {
        var memory = new FakeReasonletMemory
        {
            RememberHandler = (_, _) => Task.FromResult(Result.Failure(new MnemosyneError(MnemosyneErrorCode.Blocked, "no"))),
        };
        using var watcher = new CaptureRunWatcher();
        await using var provider = Provider(memory);

        Assert.True(Capture(provider));

        Assert.Equal("Blocked", await watcher.NextOutcomeAsync(Guard, Ct));
    }

    [Fact]
    public async Task ReturnsOutcomesInArrivalOrder()
    {
        var kinds = new Queue<ReasonletCaptureKind>([ReasonletCaptureKind.NewReasonlet, ReasonletCaptureKind.ExecutionOnly]);
        var memory = new FakeReasonletMemory
        {
            RememberHandler = (_, _) => Task.FromResult(CaptureTestData.Receipt(kinds.Dequeue())),
        };
        using var watcher = new CaptureRunWatcher();
        await using var provider = Provider(memory);

        Assert.True(Capture(provider));
        Assert.True(Capture(provider));

        Assert.Equal("new_reasonlet", await watcher.NextOutcomeAsync(Guard, Ct));
        Assert.Equal("execution_only", await watcher.NextOutcomeAsync(Guard, Ct));
    }

    [Fact]
    public async Task TimesOutWhenNothingFinishes()
    {
        using var watcher = new CaptureRunWatcher();

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => watcher.NextOutcomeAsync(TimeSpan.FromMilliseconds(50), Ct));

        Assert.Equal("No capture finished within 00:00:00.0500000.", error.Message);
    }
}
