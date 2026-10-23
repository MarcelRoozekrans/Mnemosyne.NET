using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

public sealed class MnemosyneOptionsTests
{
    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new MnemosyneOptions();

        Assert.Equal(20, options.CandidatePool);
        Assert.Equal(0.7, options.MinIntentConfidence);
        Assert.Equal(TimeSpan.FromSeconds(30), options.EvaluationTimeout);
        Assert.NotNull(options.Ranking);
        Assert.True(options.Validate().IsSuccess);
    }

    [Fact]
    public void TheBoundsAreInclusive()
    {
        Assert.True(new MnemosyneOptions { CandidatePool = 1, MinIntentConfidence = 0 }.Validate().IsSuccess);
        Assert.True(new MnemosyneOptions { CandidatePool = 200, MinIntentConfidence = 1 }.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(0, 0.7, "CandidatePool:")]
    [InlineData(201, 0.7, "CandidatePool:")]
    [InlineData(20, -0.1, "MinIntentConfidence:")]
    [InlineData(20, 1.1, "MinIntentConfidence:")]
    [InlineData(20, double.NaN, "MinIntentConfidence:")]
    public void OutOfRangeValuesFail(int pool, double confidence, string path)
    {
        var result = new MnemosyneOptions { CandidatePool = pool, MinIntentConfidence = confidence }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith(path, result.Error.Message);
    }

    public static TheoryData<TimeSpan> UsableTimeouts() =>
        [TimeSpan.FromTicks(1), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(uint.MaxValue - 1.0), Timeout.InfiniteTimeSpan];

    [Theory]
    [MemberData(nameof(UsableTimeouts))]
    public void APositiveOrInfiniteEvaluationTimeoutPasses(TimeSpan timeout) =>
        Assert.True(new MnemosyneOptions { EvaluationTimeout = timeout }.Validate().IsSuccess);

    public static TheoryData<TimeSpan> UnusableTimeouts() =>
        [TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(-5), TimeSpan.FromMilliseconds(uint.MaxValue), TimeSpan.MaxValue];

    [Theory]
    [MemberData(nameof(UnusableTimeouts))]
    public void AnyOtherEvaluationTimeoutFails(TimeSpan timeout)
    {
        var result = new MnemosyneOptions { EvaluationTimeout = timeout }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluationTimeout: must be positive and at most 4294967294 milliseconds, or Timeout.InfiniteTimeSpan", result.Error.Message);
    }

    [Fact]
    public void TheLongestTimeoutIsOneACancellationTokenSourceAccepts()
    {
        using var accepted = new CancellationTokenSource(MnemosyneOptions.MaxEvaluationTimeout, TimeProvider.System);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CancellationTokenSource(MnemosyneOptions.MaxEvaluationTimeout + TimeSpan.FromMilliseconds(1), TimeProvider.System));
    }

    [Fact]
    public void ANullRankingFails() =>
        Assert.StartsWith("Ranking:", new MnemosyneOptions { Ranking = null! }.Validate().Error.Message);

    [Fact]
    public void RankingPathsNestUnderRanking() =>
        Assert.StartsWith(
            "Ranking.PriorWeight:",
            new MnemosyneOptions { Ranking = new ReasonletRankingOptions { PriorWeight = 0 } }.Validate().Error.Message);
}
