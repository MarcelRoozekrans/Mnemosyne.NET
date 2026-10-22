using Xunit;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class LeafRecordTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    [Fact]
    public void Scope_Default_Fails() => AssertInvalid(default(ReasonletScope).Validate(), "Value");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Scope_Blank_Fails(string v) => AssertInvalid(new ReasonletScope(v).Validate(), "Value");

    [Fact]
    public void Scope_Valid_Passes() => Assert.True(new ReasonletScope("a").Validate().IsSuccess);

    [Fact]
    public void Provenance_NullSource_Fails() =>
        AssertInvalid(new ReasonletProvenance(null!, null, null, null).Validate(), "Source");

    [Fact]
    public void Provenance_BlankTagKey_ReportsIndex()
    {
        var tags = new Dictionary<string, string> { ["ok"] = "1", [" "] = "2" };
        AssertInvalid(new ReasonletProvenance("s", null, null, tags).Validate(), "Tags[1]");
    }

    [Fact]
    public void Provenance_Boundary_Passes()
    {
        Assert.True(new ReasonletProvenance("s", null, null, null).Validate().IsSuccess);
        Assert.True(new ReasonletProvenance("s", "r", "u", new Dictionary<string, string> { ["k"] = "v" }).Validate().IsSuccess);
    }

    [Fact]
    public void Outcome_NaNScore_Fails() =>
        AssertInvalid(new ReasonletOutcome(true, TimeSpan.Zero, double.NaN).Validate(), "Score");

    [Fact]
    public void Outcome_NegativeDuration_Fails() =>
        AssertInvalid(new ReasonletOutcome(true, TimeSpan.FromSeconds(-1), null).Validate(), "Duration");

    [Fact]
    public void Outcome_Boundary_Passes()
    {
        Assert.True(new ReasonletOutcome(true, TimeSpan.Zero, 0).Validate().IsSuccess);
        Assert.True(new ReasonletOutcome(false, TimeSpan.Zero, 1).Validate().IsSuccess);
        Assert.True(new ReasonletOutcome(false, TimeSpan.Zero, null).Validate().IsSuccess);
    }

    [Theory]
    [InlineData(1.5, 0.5, 0.5, "Utility")]
    [InlineData(0.5, -1, 0.5, "Correctness")]
    [InlineData(0.5, 0.5, 1.5, "Reusability")]
    public void Feedback_Invalid_Fails(double u, double c, double r, string path) =>
        AssertInvalid(new ReasonletFeedback(u, c, r).Validate(), path);

    [Fact]
    public void Feedback_Boundary_Passes()
    {
        Assert.True(new ReasonletFeedback(0, 0, 0).Validate().IsSuccess);
        Assert.True(new ReasonletFeedback(1, 1, 1).Validate().IsSuccess);
    }

    [Theory]
    [InlineData(null, 0.5, "Intent")]
    [InlineData(" ", 0.5, "Intent")]
    [InlineData("i", double.NaN, "Confidence")]
    [InlineData("i", 2.0, "Confidence")]
    public void Intent_Invalid_Fails(string? intent, double conf, string path) =>
        AssertInvalid(new ReasonletIntent(intent!, conf).Validate(), path);

    [Fact]
    public void Intent_Boundary_Passes()
    {
        Assert.True(new ReasonletIntent("i", 0).Validate().IsSuccess);
        Assert.True(new ReasonletIntent("i", 1).Validate().IsSuccess);
    }

    [Theory]
    [InlineData(2, 3, "Successes")]
    [InlineData(-1, 0, "Runs")]
    [InlineData(2, -1, "Successes")]
    public void Metrics_Invalid_Fails(int runs, int successes, string path) =>
        AssertInvalid(new ReasonletMetrics(runs, successes, null).Validate(), path);

    [Fact]
    public void Metrics_Boundary_Passes()
    {
        Assert.True(new ReasonletMetrics(0, 0, null).Validate().IsSuccess);
        Assert.True(new ReasonletMetrics(2, 2, DateTimeOffset.UnixEpoch).Validate().IsSuccess);
    }

    [Theory]
    [InlineData(1.5, 0.5, 0.5, 0.5, "Similarity")]
    [InlineData(0.5, -1, 0.5, 0.5, "SuccessRate")]
    [InlineData(0.5, 0.5, double.PositiveInfinity, 0.5, "Freshness")]
    [InlineData(0.5, 0.5, 0.5, double.NaN, "Total")]
    public void Score_Invalid_Fails(double s, double r, double f, double t, string path) =>
        AssertInvalid(new ReasonletScore(s, r, f, t).Validate(), path);

    [Fact]
    public void Score_Boundary_Passes()
    {
        Assert.True(new ReasonletScore(0, 0, 0, 0).Validate().IsSuccess);
        Assert.True(new ReasonletScore(1, 1, 1, 1).Validate().IsSuccess);
    }
}
