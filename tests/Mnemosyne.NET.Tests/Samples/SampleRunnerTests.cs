using Mnemosyne.NET.Abstractions;
using Mnemosyne.Samples;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Samples;

// The samples run in CI as checks, so the runner must turn every way a sample can end into an exit code and a
// FAILED line, and never let an exception escape as a crash.
public sealed class SampleRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ReturnsTheBodysCodeAndWritesNothingOnSuccess()
    {
        var error = new StringWriter();

        var code = await SampleRunner.RunAsync(_ => Task.FromResult(0), Generous, error);

        Assert.Equal(0, code);
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
