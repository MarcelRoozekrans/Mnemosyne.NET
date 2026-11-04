using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

public sealed class ReasonletCaptureOptionsTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = new ReasonletCaptureOptions();

        Assert.Equal(256, options.Capacity);
        Assert.Equal("mnemosyne.chat", options.Source);
        Assert.Equal(TimeSpan.FromSeconds(30), options.DrainTimeout);
        Assert.True(options.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public void CapacityOutOfRangeFails(int capacity)
    {
        var result = new ReasonletCaptureOptions { Capacity = capacity }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Capacity: must be between 1 and 100000", result.Error.Message);
    }

    [Fact]
    public void CapacityBoundsPass()
    {
        Assert.True(new ReasonletCaptureOptions { Capacity = 1 }.Validate().IsSuccess);
        Assert.True(new ReasonletCaptureOptions { Capacity = 100000 }.Validate().IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankSourceFails(string source)
    {
        var result = new ReasonletCaptureOptions { Source = source }.Validate();

        Assert.Equal("Source: must not be blank", result.Error.Message);
    }

    [Fact]
    public void ANonPositiveDrainTimeoutFails()
    {
        var result = new ReasonletCaptureOptions { DrainTimeout = TimeSpan.Zero }.Validate();

        Assert.StartsWith("DrainTimeout:", result.Error.Message, StringComparison.Ordinal);
        Assert.True(new ReasonletCaptureOptions { DrainTimeout = Timeout.InfiniteTimeSpan }.Validate().IsSuccess);
    }
}
