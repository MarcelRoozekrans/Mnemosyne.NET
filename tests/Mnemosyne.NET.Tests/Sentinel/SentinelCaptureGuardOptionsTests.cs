using AI.Sentinel.Detection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Sentinel;
using Xunit;

namespace Mnemosyne.NET.Tests.Sentinel;

public sealed class SentinelCaptureGuardOptionsTests
{
    [Fact]
    public void DefaultsAreHighUnnamedAndAiSentinel()
    {
        var options = new SentinelCaptureGuardOptions();

        Assert.Equal(Severity.High, options.BlockAtSeverity);
        Assert.Null(options.PipelineName);
        Assert.Equal("ai-sentinel", options.GuardName);
        Assert.True(options.Validate().IsSuccess);
    }

    [Fact]
    public void NoneSeverityIsInvalid() =>
        AssertFails(new SentinelCaptureGuardOptions { BlockAtSeverity = Severity.None }, "BlockAtSeverity");

    [Fact]
    public void UndefinedSeverityIsInvalid() =>
        AssertFails(new SentinelCaptureGuardOptions { BlockAtSeverity = (Severity)42 }, "BlockAtSeverity");

    [Fact]
    public void BlankPipelineNameIsInvalid() =>
        AssertFails(new SentinelCaptureGuardOptions { PipelineName = " " }, "PipelineName");

    [Fact]
    public void ABlankGuardNameIsInvalid() =>
        AssertFails(new SentinelCaptureGuardOptions { GuardName = " " }, "GuardName");

    [Fact]
    public void AGuardNameWithALoneSurrogateIsInvalid() =>
        AssertFails(new SentinelCaptureGuardOptions { GuardName = "\uD800" }, "GuardName");

    [Fact]
    public void AGuardNameOverAHundredCodePointsIsInvalid() =>
        AssertFails(new SentinelCaptureGuardOptions { GuardName = new string('x', 101) }, "GuardName");

    [Theory]
    [InlineData(Severity.Low)]
    [InlineData(Severity.Medium)]
    [InlineData(Severity.High)]
    [InlineData(Severity.Critical)]
    public void EveryRealSeverityIsValid(Severity severity) =>
        Assert.True(new SentinelCaptureGuardOptions { BlockAtSeverity = severity }.Validate().IsSuccess);

    private static void AssertFails(SentinelCaptureGuardOptions options, string path)
    {
        var result = options.Validate();

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith(path + ": ", result.Error.Message, StringComparison.Ordinal);
    }
}
