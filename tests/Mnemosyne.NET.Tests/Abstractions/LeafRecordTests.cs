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
        AssertInvalid(new ReasonletProvenance { Source = null! }.Validate(), "Source");

    [Fact]
    public void Provenance_BlankTagKey_ReportsIndex()
    {
        var tags = new Dictionary<string, string> { ["ok"] = "1", [" "] = "2" };
        AssertInvalid(new ReasonletProvenance { Source = "s", Tags = tags }.Validate(), "Tags[\" \"]");
    }

    [Fact]
    public void Provenance_EmptyTagKey_ReportsKey()
    {
        var tags = new Dictionary<string, string> { [""] = "v" };
        AssertInvalid(new ReasonletProvenance { Source = "s", Tags = tags }.Validate(), "Tags[\"\"]");
    }

    [Fact]
    public void Provenance_NullTagValue_ReportsKey()
    {
        var tags = new Dictionary<string, string> { ["k"] = null! };
        AssertInvalid(new ReasonletProvenance { Source = "s", Tags = tags }.Validate(), "Tags[\"k\"]");
    }

    [Fact]
    public void Provenance_BlankTagValue_Passes()
    {
        var tags = new Dictionary<string, string> { ["k"] = " " };
        Assert.True(new ReasonletProvenance { Source = "s", Tags = tags }.Validate().IsSuccess);
    }

    [Fact]
    public void Provenance_TagKeyWithQuoteAndBackslash_IsEscapedInPath()
    {
        var tags = new Dictionary<string, string> { ["a\"b\\c"] = null! };
        AssertInvalid(new ReasonletProvenance { Source = "s", Tags = tags }.Validate(), "Tags[\"a\\\"b\\\\c\"]");
    }

    [Fact]
    public void Provenance_TagKeyWithNewline_IsEscapedInPath()
    {
        var tags = new Dictionary<string, string> { ["a\nb"] = null! };
        AssertInvalid(new ReasonletProvenance { Source = "s", Tags = tags }.Validate(), "Tags[\"a\\u000Ab\"]");
    }

    [Fact]
    public void Provenance_NullTagKey_ReportsNull()
    {
        var tags = new NullKeyTags();
        AssertInvalid(new ReasonletProvenance { Source = "s", Tags = tags }.Validate(), "Tags[null]");
    }

    private sealed class NullKeyTags : IReadOnlyDictionary<string, string>
    {
        private static readonly KeyValuePair<string, string>[] Items = [new(null!, "v")];
        public string this[string key] => throw new KeyNotFoundException();
        public IEnumerable<string> Keys => [null!];
        public IEnumerable<string> Values => ["v"];
        public int Count => 1;
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string value) { value = null!; return false; }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)Items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Intent_Default_Fails() => AssertInvalid(default(ReasonletIntent).Validate(), "Intent");

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.True(default(ReasonletFeedback).Validate().IsSuccess);
        Assert.True(default(ReasonletMetrics).Validate().IsSuccess);
        Assert.True(default(ReasonletOutcome).Validate().IsSuccess);
        Assert.True(default(ReasonletScore).Validate().IsSuccess);
    }

    [Fact]
    public void Provenance_Boundary_Passes()
    {
        Assert.True(new ReasonletProvenance { Source = "s" }.Validate().IsSuccess);
        Assert.True(new ReasonletProvenance { Source = "s", RunId = "r", SubjectId = "u", Tags = new Dictionary<string, string> { ["k"] = "v" } }.Validate().IsSuccess);
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
        AssertInvalid(new ReasonletMetrics(runs, successes, null, DateTimeOffset.UnixEpoch).Validate(), path);

    [Fact]
    public void Metrics_Boundary_Passes()
    {
        Assert.True(default(ReasonletMetrics).Validate().IsSuccess);
        Assert.True(new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch).Validate().IsSuccess);
        Assert.True(new ReasonletMetrics(2, 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch).Validate().IsSuccess);
    }

    [Fact]
    public void Metrics_LastExecutedAt_RoundTrips()
    {
        var at = new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.Zero);
        Assert.Equal(at, new ReasonletMetrics(1, 0, null, at).LastExecutedAt);
        Assert.Equal(default, default(ReasonletMetrics).LastExecutedAt);
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

    [Fact]
    public void Retention_OneWindow_Passes()
    {
        Assert.True(new ReasonletRetention { ExecutionsOlderThan = TimeSpan.FromDays(1) }.Validate().IsSuccess);
        Assert.True(new ReasonletRetention { ReasonletsUnusedFor = TimeSpan.FromTicks(1) }.Validate().IsSuccess);
        var both = new ReasonletRetention { Scope = new ReasonletScope("s"), ExecutionsOlderThan = TimeSpan.MaxValue, ReasonletsUnusedFor = TimeSpan.FromDays(1) };
        Assert.True(both.Validate().IsSuccess);

        // Init-only, so a with-expression copies the rest: the scope and the other window stay.
        Assert.True((both with { ExecutionsOlderThan = null }).Validate().IsSuccess);
    }

    [Fact]
    public void Retention_NoWindow_Fails() =>
        AssertInvalid(new ReasonletRetention { Scope = new ReasonletScope("s") }.Validate(), "ExecutionsOlderThan");

    [Theory]
    [InlineData(0L, null, "ExecutionsOlderThan")]
    [InlineData(-1L, null, "ExecutionsOlderThan")]
    [InlineData(null, 0L, "ReasonletsUnusedFor")]
    [InlineData(null, -1L, "ReasonletsUnusedFor")]
    [InlineData(1L, 0L, "ReasonletsUnusedFor")]
    public void Retention_NotPositiveWindow_Fails(long? executionTicks, long? reasonletTicks, string path) =>
        AssertInvalid(
            new ReasonletRetention
            {
                ExecutionsOlderThan = executionTicks is { } e ? TimeSpan.FromTicks(e) : null,
                ReasonletsUnusedFor = reasonletTicks is { } r ? TimeSpan.FromTicks(r) : null,
            }.Validate(),
            path);

    [Fact]
    public void Retention_DefaultScope_Fails() =>
        AssertInvalid(
            new ReasonletRetention { Scope = default(ReasonletScope), ExecutionsOlderThan = TimeSpan.FromDays(1) }.Validate(),
            "Scope.Value");
}
