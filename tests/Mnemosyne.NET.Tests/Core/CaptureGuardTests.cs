using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The capture guard step of remember: it runs after validation and before anything else touches the capture.</summary>
public sealed class CaptureGuardTests
{
    private const string QuestionText = "question text 9c1e";

    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();
    private readonly FakeIntentExtractor _extractor = new();
    private readonly FakeEvaluator _evaluator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ReasonletMemory Memory(ICaptureGuard? guard, MnemosyneOptions? options = null, TimeProvider? clock = null) =>
        new(_store, _selector, _generator, _extractor, _evaluator, options ?? new MnemosyneOptions(), clock, guard);

    private static ReasonletCapture Capture() => TestData.Capture() with { Question = QuestionText, Answer = "answer" };

    private void AssertNothingDownstream()
    {
        Assert.Equal(0, _store.Calls);
        Assert.Empty(_extractor.Questions);
        Assert.Equal(0, _generator.Calls);
        Assert.Empty(_evaluator.Requests);
    }

    private static void AssertNoCaptureText(MnemosyneError error)
    {
        Assert.DoesNotContain(QuestionText, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionText, error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoGuardRemembersAsBefore()
    {
        var result = await Memory(guard: null).RememberAsync(Capture(), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _store.Calls);
        Assert.Single(_extractor.Questions);
        Assert.Equal(1, _generator.Calls);
        Assert.Single(_evaluator.Requests);
    }

    [Fact]
    public async Task AnAllowedCaptureIsStored()
    {
        var guard = new FakeCaptureGuard();
        var capture = Capture();

        var result = await Memory(guard).RememberAsync(capture, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _store.Calls);
        Assert.Equal(1, guard.Calls);
        var seen = Assert.Single(guard.Seen);
        Assert.Same(capture, seen);
        Assert.Null(seen.Intent);
        Assert.Null(seen.Name);
    }

    [Fact]
    public async Task ABlockedCaptureIsDropped()
    {
        var guard = new FakeCaptureGuard { Respond = () => CaptureVerdict.Block("redactor", "pii.email") };

        var result = await Memory(guard).RememberAsync(Capture(), Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal("A capture guard blocked the capture.", result.Error.Message);
        Assert.Equal("redactor/pii.email", result.Error.Detail);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task ABlockedRerunRecordsNoExecution()
    {
        var guard = new FakeCaptureGuard { Respond = () => CaptureVerdict.Block("redactor", "pii.email") };

        var result = await Memory(guard).RememberAsync(Capture() with { ParentId = ReasonletId.New() }, Ct);

        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal(0, _store.Calls);
        Assert.Null(_store.Remembered);
        Assert.Null(_store.Executed);
    }

    [Fact]
    public async Task AnUnavailableGuardDropsTheCapture()
    {
        var guard = new FakeCaptureGuard
        {
            Respond = () => new MnemosyneError(MnemosyneErrorCode.Unavailable, $"guard said: {QuestionText}", $"detail: {QuestionText}"),
        };

        var result = await Memory(guard).RememberAsync(Capture(), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal("The capture guard failed.", result.Error.Message);
        Assert.Null(result.Error.Detail);
        AssertNoCaptureText(result.Error);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task AThrowingGuardDropsTheCapture()
    {
        var guard = new FakeCaptureGuard { Respond = () => throw new InvalidDataException(QuestionText) };

        var result = await Memory(guard).RememberAsync(Capture(), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal("The capture guard failed.", result.Error.Message);
        Assert.Equal("System.IO.InvalidDataException", result.Error.Detail);
        AssertNoCaptureText(result.Error);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task AGuardThatIgnoresItsTokenTimesOut()
    {
        var clock = new ManualTimeProvider();
        var guard = new FakeCaptureGuard { Hang = true, OnCall = clock.Fire };
        var memory = Memory(guard, new MnemosyneOptions { GuardTimeout = TimeSpan.FromSeconds(1) }, clock);

        // The real-time bound only catches a hang; the manual clock decides when the timeout expires.
        var result = await memory.RememberAsync(Capture(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal("The capture guard failed.", result.Error.Message);
        Assert.Contains(TimeSpan.FromSeconds(1), clock.DueTimes);
        Assert.Equal(1, guard.Calls);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task AGuardTimeoutIsSeparateFromTheEvaluationTimeout()
    {
        var clock = new ManualTimeProvider();
        var guard = new FakeCaptureGuard { Hang = true, OnCall = clock.Fire };
        var options = new MnemosyneOptions { EvaluationTimeout = Timeout.InfiniteTimeSpan, GuardTimeout = TimeSpan.FromSeconds(1) };

        var result = await Memory(guard, options, clock).RememberAsync(Capture(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal([TimeSpan.FromSeconds(1)], clock.DueTimes);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task AnotherErrorCodeIsAnAdapterBug()
    {
        var guard = new FakeCaptureGuard
        {
            Respond = () => new MnemosyneError(MnemosyneErrorCode.Validation, $"guard said: {QuestionText}", $"detail: {QuestionText}"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(guard).RememberAsync(Capture(), Ct));

        Assert.Contains(nameof(MnemosyneErrorCode.Validation), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionText, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("guard said", ex.Message, StringComparison.Ordinal);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task ADefaultVerdictIsAnAdapterBug()
    {
        var guard = new FakeCaptureGuard { Respond = () => default(CaptureVerdict) };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(guard).RememberAsync(Capture(), Ct));

        AssertNothingDownstream();
    }

    [Fact]
    public async Task TheCallersCancellationPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var first = new FakeCaptureGuard();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Memory(first).RememberAsync(Capture(), cancelled.Token));
        Assert.Equal(0, first.Calls);
        AssertNothingDownstream();

        using var cts = new CancellationTokenSource();
        var second = new FakeCaptureGuard { Hang = true, OnCall = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Memory(second, clock: new ManualTimeProvider()).RememberAsync(Capture(), cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Equal(1, second.Calls);
        AssertNothingDownstream();
    }

    [Fact]
    public async Task AnInvalidCaptureNeverReachesTheGuard()
    {
        var guard = new FakeCaptureGuard();

        var result = await Memory(guard).RememberAsync(Capture() with { Question = " " }, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(0, guard.Calls);
        AssertNothingDownstream();
    }
}
