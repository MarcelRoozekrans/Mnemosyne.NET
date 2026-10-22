using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class EnumNumberingTests
{
    [Fact]
    public void ErrorCodes_HavePinnedValues()
    {
        Assert.Equal(1, (int)MnemosyneErrorCode.Validation);
        Assert.Equal(2, (int)MnemosyneErrorCode.NotFound);
        Assert.Equal(3, (int)MnemosyneErrorCode.Conflict);
        Assert.Equal(4, (int)MnemosyneErrorCode.Unavailable);
        Assert.Equal(4, Enum.GetValues<MnemosyneErrorCode>().Length);
    }

    [Fact]
    public void Statuses_HavePinnedValues()
    {
        Assert.Equal(1, (int)ReasonletStatus.Active);
        Assert.Equal(2, (int)ReasonletStatus.PendingReview);
        Assert.Equal(2, Enum.GetValues<ReasonletStatus>().Length);
    }

    [Fact]
    public void CaptureKinds_HavePinnedValues()
    {
        Assert.Equal(1, (int)ReasonletCaptureKind.NewReasonlet);
        Assert.Equal(2, (int)ReasonletCaptureKind.NewVersion);
        Assert.Equal(3, (int)ReasonletCaptureKind.ExecutionOnly);
        Assert.Equal(3, Enum.GetValues<ReasonletCaptureKind>().Length);
    }

    [Fact]
    public void DefaultResults_CarryAnUndefinedErrorCode()
    {
        var unit = default(UnitResult<MnemosyneError>);
        var valued = default(Result<int, MnemosyneError>);
        Assert.True(unit.IsFailure);
        Assert.True(valued.IsFailure);
        Assert.False(Enum.IsDefined(unit.Error.Code));
        Assert.False(Enum.IsDefined(valued.Error.Code));
    }

    [Fact]
    public void Definition_DefaultStatus_Fails()
    {
        var r = (TestData.Definition() with { Status = default }).Validate();
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith("Status: ", r.Error.Message);
    }
}
