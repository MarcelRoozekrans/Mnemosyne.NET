using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

public sealed class VariantSelectionOptionsTests
{
    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new VariantSelectionOptions();

        Assert.Equal(VariantExploration.None, options.Exploration);
        Assert.Null(options.Seed);
        Assert.Equal(0, options.MinTrials);
        Assert.True(options.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void TheMinTrialsBoundsAreInclusive(int minTrials) =>
        Assert.True(new VariantSelectionOptions { MinTrials = minTrials }.Validate().IsSuccess);

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void MinTrialsOutOfRangeFails(int minTrials)
    {
        var result = new VariantSelectionOptions { MinTrials = minTrials }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("MinTrials:", result.Error.Message);
    }

    [Fact]
    public void AnUndefinedExplorationFails()
    {
        var result = new VariantSelectionOptions { Exploration = (VariantExploration)99 }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Exploration:", result.Error.Message);
    }

    [Theory]
    [InlineData(VariantExploration.None)]
    [InlineData(VariantExploration.Thompson)]
    public void EveryDefinedExplorationPasses(VariantExploration exploration) =>
        Assert.True(new VariantSelectionOptions { Exploration = exploration }.Validate().IsSuccess);

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void AnySeedPasses(int seed) =>
        Assert.True(new VariantSelectionOptions { Seed = seed }.Validate().IsSuccess);
}
