using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Minos;
using Xunit;

namespace Mnemosyne.NET.Tests.Minos;

public sealed class MinosEvaluatorOptionsTests
{
    private const string Emoji = "\U0001F600";

    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new MinosEvaluatorOptions();

        Assert.Equal(0.5, options.SuccessThreshold);
        Assert.Equal("minos", options.EvaluatorName);
        Assert.Null(options.DecisionClientKey);
        Assert.True(options.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void TheThresholdBoundsAreInclusive(double threshold) =>
        Assert.True(new MinosEvaluatorOptions { SuccessThreshold = threshold }.Validate().IsSuccess);

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void AnOutOfRangeThresholdFails(double threshold)
    {
        var result = new MinosEvaluatorOptions { SuccessThreshold = threshold }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("SuccessThreshold: must be a finite number between 0 and 1", result.Error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankEvaluatorNameFails(string? name)
    {
        var result = new MinosEvaluatorOptions { EvaluatorName = name! }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluatorName: must not be blank", result.Error.Message);
    }

    [Fact]
    public void AnEvaluatorNameWithALoneSurrogateFails()
    {
        var result = new MinosEvaluatorOptions { EvaluatorName = "judge\uD800" }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluatorName: must be valid UTF-16", result.Error.Message);
    }

    [Fact]
    public void AnEvaluatorNameOfAHundredCodePointsPasses()
    {
        Assert.True(new MinosEvaluatorOptions { EvaluatorName = new string('x', 100) }.Validate().IsSuccess);

        // Surrogate pairs count once, the unit the evaluation's own limit counts.
        Assert.True(new MinosEvaluatorOptions { EvaluatorName = string.Concat(Enumerable.Repeat(Emoji, 100)) }.Validate().IsSuccess);
    }

    [Fact]
    public void AnEvaluatorNameOverAHundredCodePointsFails()
    {
        var result = new MinosEvaluatorOptions { EvaluatorName = string.Concat(Enumerable.Repeat(Emoji, 101)) }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluatorName: must not be longer than 100 code points", result.Error.Message);
    }

    [Fact]
    public void AnyDecisionClientKeyIsValid() =>
        Assert.True(new MinosEvaluatorOptions { DecisionClientKey = "judge" }.Validate().IsSuccess);

    [Fact]
    public void AValidNameIsAValidEvaluation()
    {
        // The options accept exactly the names a verdict accepts, so a valid option never yields an invalid verdict.
        foreach (var name in new[] { "minos", new string('x', 100), string.Concat(Enumerable.Repeat(Emoji, 100)) })
        {
            Assert.True(new MinosEvaluatorOptions { EvaluatorName = name }.Validate().IsSuccess);
            Assert.True(new ReasonletEvaluation(true, 1, name).Validate().IsSuccess);
        }
    }
}
