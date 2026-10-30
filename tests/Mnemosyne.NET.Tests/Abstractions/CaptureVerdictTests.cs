using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

/// <summary>The capture guard's verdict: the two factories and their validation.</summary>
public class CaptureVerdictTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    [Fact]
    public void AllowIsValidAndNotBlocked()
    {
        var v = CaptureVerdict.Allow("g");
        Assert.False(v.IsBlocked);
        Assert.Null(v.Detector);
        Assert.Equal("g", v.Guard);
        Assert.True(v.Validate().IsSuccess);
    }

    [Fact]
    public void BlockCarriesTheDetector()
    {
        var v = CaptureVerdict.Block("g", "pii.email");
        Assert.True(v.IsBlocked);
        Assert.Equal("pii.email", v.Detector);
        Assert.Equal("g", v.Guard);
        Assert.True(v.Validate().IsSuccess);
    }

    [Fact]
    public void DefaultFailsValidation() =>
        AssertInvalid(default(CaptureVerdict).Validate(), "Guard");

    // A lone surrogate cannot ride in InlineData, since the attribute encoding replaces it, so the cases come from a member.
    public static TheoryData<string> BlankOrInvalidNames() => new() { "", " ", "\uD800" };

    [Theory]
    [MemberData(nameof(BlankOrInvalidNames))]
    public void BlankGuardOrDetectorFails(string bad)
    {
        AssertInvalid(CaptureVerdict.Allow(bad).Validate(), "Guard");
        AssertInvalid(CaptureVerdict.Block(bad, "d").Validate(), "Guard");
        AssertInvalid(CaptureVerdict.Block("g", bad).Validate(), "Detector");
    }

    [Fact]
    public void OverlongNamesFail()
    {
        var ok = new string('x', 100);
        var tooLong = new string('x', 101);
        Assert.True(CaptureVerdict.Allow(ok).Validate().IsSuccess);
        Assert.True(CaptureVerdict.Block(ok, ok).Validate().IsSuccess);
        AssertInvalid(CaptureVerdict.Allow(tooLong).Validate(), "Guard");
        AssertInvalid(CaptureVerdict.Block(tooLong, "d").Validate(), "Guard");
        AssertInvalid(CaptureVerdict.Block("g", tooLong).Validate(), "Detector");
    }
}
