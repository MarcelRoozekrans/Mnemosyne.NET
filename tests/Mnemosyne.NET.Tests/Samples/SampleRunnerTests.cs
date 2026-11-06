using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Chat;
using Mnemosyne.Samples;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Samples;

// The samples run in CI as checks, so the runner must turn every way a sample can end into an exit code and a
// FAILED line, and never let an exception escape as a crash.
public sealed class SampleRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReturnsTheBodysCodeAndWritesNothing(int bodyCode)
    {
        var error = new StringWriter();

        var code = await SampleRunner.RunAsync(_ => Task.FromResult(bodyCode), Generous, error);

        Assert.Equal(bodyCode, code);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ASampleFailureWritesFailedAndReturnsOne()
    {
        var error = new StringWriter();

        var code = await SampleRunner.RunAsync(_ => throw new SampleFailure("the receipts differ"), Generous, error);

        Assert.Equal(1, code);
        Assert.Equal($"FAILED: the receipts differ{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task ATimeoutExceptionWritesItsMessageAndReturnsOne()
    {
        var error = new StringWriter();

        var code = await SampleRunner.RunAsync(_ => throw new TimeoutException("no capture finished"), Generous, error);

        Assert.Equal(1, code);
        Assert.Equal($"FAILED: no capture finished{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task AnUnexpectedExceptionWritesTypeAndMessageAndReturnsOne()
    {
        var error = new StringWriter();

        var code = await SampleRunner.RunAsync(_ => throw new InvalidOperationException("boom"), Generous, error);

        Assert.Equal(1, code);
        Assert.Equal($"FAILED: InvalidOperationException: boom{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task TheLimitWritesTheTwoMinuteMessage()
    {
        var error = new StringWriter();
        var limit = TimeSpan.FromMilliseconds(50);

        var code = await SampleRunner.RunAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, limit, error);

        Assert.Equal(1, code);
        Assert.Equal($"FAILED: the sample did not finish within {limit.TotalMinutes} minutes.{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task ACancellationNotCausedByTheLimitIsReportedAsUnexpected()
    {
        var error = new StringWriter();

        var code = await SampleRunner.RunAsync(_ => throw new OperationCanceledException("cancelled elsewhere"), Generous, error);

        Assert.Equal(1, code);
        Assert.Equal($"FAILED: OperationCanceledException: cancelled elsewhere{Environment.NewLine}", error.ToString());
    }

    private static readonly ReasonletScope Scope = new("sample-scope");

    private static Result<ReasonletErasureResult, MnemosyneError> Erased() =>
        Result<ReasonletErasureResult, MnemosyneError>.Success(new ReasonletErasureResult { Reasonlets = 3, Executions = 12 });

    private static Result<ReasonletErasureResult, MnemosyneError> EraseFailed() =>
        Result<ReasonletErasureResult, MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Unavailable, "the store is down"));

    [Fact]
    public async Task TryEraseReportsASuccess()
    {
        var memory = new FakeReasonletMemory { EraseHandler = (_, _) => Task.FromResult(Erased()) };
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.True(await SampleRunner.TryEraseAsync(memory, Scope, output, error));

        Assert.Equal($"Erased the scope: 3 Reasonlets and 12 executions deleted.{Environment.NewLine}", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task TryEraseReportsAFailureResult()
    {
        var memory = new FakeReasonletMemory { EraseHandler = (_, _) => Task.FromResult(EraseFailed()) };
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.False(await SampleRunner.TryEraseAsync(memory, Scope, output, error));

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal($"Erase failed: Unavailable: the store is down{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task TryEraseReportsAThrowingEraseAndDoesNotThrow()
    {
        var memory = new FakeReasonletMemory { EraseHandler = (_, _) => throw new InvalidOperationException("connection lost") };
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.False(await SampleRunner.TryEraseAsync(memory, Scope, output, error));

        Assert.Equal($"Erase threw InvalidOperationException: connection lost{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task TryEraseUsesNoTokenSoATimedOutRunStillCleansUp()
    {
        CancellationToken seen = new(true);
        var memory = new FakeReasonletMemory
        {
            EraseHandler = (_, ct) =>
            {
                seen = ct;
                return Task.FromResult(Erased());
            },
        };

        await SampleRunner.TryEraseAsync(memory, Scope, new StringWriter(), new StringWriter());

        Assert.Equal(CancellationToken.None, seen);
    }

    [Fact]
    public async Task EraseAfterKeepsTheWalkthroughsCodeWhenTheEraseWorks()
    {
        var memory = new FakeReasonletMemory { EraseHandler = (_, _) => Task.FromResult(Erased()) };

        Assert.Equal(0, await SampleRunner.EraseAfterAsync(memory, Scope, new StringWriter(), new StringWriter(), () => Task.FromResult(0)));
        Assert.Equal(1, await SampleRunner.EraseAfterAsync(memory, Scope, new StringWriter(), new StringWriter(), () => Task.FromResult(1)));
    }

    [Fact]
    public async Task EraseAfterReturnsOneWhenAPassingWalkthroughHasAFailingErase()
    {
        var memory = new FakeReasonletMemory { EraseHandler = (_, _) => Task.FromResult(EraseFailed()) };

        var code = await SampleRunner.EraseAfterAsync(memory, Scope, new StringWriter(), new StringWriter(), () => Task.FromResult(0));

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task EraseAfterErasesAndLetsTheWalkthroughsFailureThroughWhenTheEraseAlsoFails()
    {
        var erased = 0;
        var memory = new FakeReasonletMemory
        {
            EraseHandler = (_, _) =>
            {
                erased++;
                return Task.FromResult(EraseFailed());
            },
        };

        await Assert.ThrowsAsync<SampleFailure>(() =>
            SampleRunner.EraseAfterAsync(memory, Scope, new StringWriter(), new StringWriter(), () => throw new SampleFailure("the receipts differ")));

        Assert.Equal(1, erased);
    }

    [Fact]
    public void RequireReturnsTheValueOfASuccessAndThrowsTheFailureWording()
    {
        Assert.Equal(7, SampleRunner.Require(Result<int, MnemosyneError>.Success(7), "Recall"));

        var failure = Result<int, MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Unavailable, "the store is down"));
        var thrown = Assert.Throws<SampleFailure>(() => SampleRunner.Require(failure, "Recall"));
        Assert.Equal("Recall failed: Unavailable: the store is down", thrown.Message);
    }

    [Fact]
    public void RequireSuccessThrowsTheFailureWording()
    {
        SampleRunner.RequireSuccess(UnitResult<MnemosyneError>.Success(), "Migrate");

        var failure = UnitResult<MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Unavailable, "the store is down"));
        var thrown = Assert.Throws<SampleFailure>(() => SampleRunner.RequireSuccess(failure, "Migrate"));
        Assert.Equal("Migrate failed: Unavailable: the store is down", thrown.Message);
    }

    [Fact]
    public void RequireEqualThrowsWhenTheValuesDiffer()
    {
        SampleRunner.RequireEqual(2, 2, "The runs");

        var thrown = Assert.Throws<SampleFailure>(() => SampleRunner.RequireEqual(1, 2, "The runs"));
        Assert.Equal("The runs was 1, not 2.", thrown.Message);
    }
}
