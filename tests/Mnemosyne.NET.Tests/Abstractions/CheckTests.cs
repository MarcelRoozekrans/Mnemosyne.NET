using Xunit;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class CheckTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith("x: ", r.Error.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void UnitInterval_Accepts(double v) => Assert.True(Check.UnitInterval(v, "x").IsSuccess);

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void UnitInterval_Rejects(double v) => AssertInvalid(Check.UnitInterval(v, "x"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NotBlank_Rejects(string? v) => AssertInvalid(Check.NotBlank(v, "x"));

    [Fact]
    public void NotBlank_Accepts() => Assert.True(Check.NotBlank("a", "x").IsSuccess);

    [Fact]
    public void Range_Bounds()
    {
        AssertInvalid(Check.Range(0, 1, 50, "x"));
        Assert.True(Check.Range(50, 1, 50, "x").IsSuccess);
    }

    [Fact]
    public void NotNegative_Rejects() => AssertInvalid(Check.NotNegative(TimeSpan.FromTicks(-1), "x"));

    [Fact]
    public void NotNegative_AcceptsZero() => Assert.True(Check.NotNegative(TimeSpan.Zero, "x").IsSuccess);

    [Fact]
    public void NotDefault_Rejects()
    {
        AssertInvalid(Check.NotDefault(default(ReasonletId), "x"));
        AssertInvalid(Check.NotDefault(default(ExecutionId), "x"));
        Assert.True(Check.NotDefault(ReasonletId.New(), "x").IsSuccess);
        Assert.True(Check.NotDefault(ExecutionId.New(), "x").IsSuccess);
    }

    [Fact]
    public void NotNull_Rejects()
    {
        AssertInvalid(Check.NotNull(null, "x"));
        Assert.True(Check.NotNull(new object(), "x").IsSuccess);
    }

    [Fact]
    public void Nested_PrefixesPath()
    {
        var r = Check.Nested(Check.UnitInterval(2, "Score"), "Outcome");
        Assert.True(r.IsFailure);
        Assert.StartsWith("Outcome.Score: ", r.Error.Message);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
    }

    [Fact]
    public void Nested_PassesSuccessThrough() =>
        Assert.True(Check.Nested(Check.UnitInterval(0.5, "Score"), "Outcome").IsSuccess);

    [Fact]
    public void All_ReturnsFirstFailure()
    {
        var r = Check.All(
            () => Check.NotBlank("ok", "a"),
            () => Check.NotBlank("", "b"),
            () => Check.NotBlank("", "c"));
        Assert.True(r.IsFailure);
        Assert.StartsWith("b: ", r.Error.Message);
    }

    [Fact]
    public void All_AllPass() => Assert.True(Check.All(() => Check.NotBlank("ok", "a")).IsSuccess);
}
