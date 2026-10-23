using Xunit;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class CaptureAndQueryTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    [Fact]
    public void Capture_Valid_Passes() => Assert.True(TestData.Capture().Validate().IsSuccess);

    [Fact]
    public void Capture_BadOutcomeScore_ReportsNestedPath() =>
        AssertInvalid((TestData.Capture() with { Outcome = new ReasonletOutcome(true, TimeSpan.Zero, -1) }).Validate(), "Outcome.Score");

    [Fact]
    public void Capture_BlankProvenanceSource_ReportsNestedPath() =>
        AssertInvalid((TestData.Capture() with { Provenance = new ReasonletProvenance { Source = " " } }).Validate(), "Provenance.Source");

    [Fact]
    public void Capture_BlankProvenanceTagKey_ReportsNestedPath()
    {
        var tags = new Dictionary<string, string> { [" "] = "v" };
        AssertInvalid((TestData.Capture() with { Provenance = new ReasonletProvenance { Source = "s", Tags = tags } }).Validate(), "Provenance.Tags[\" \"]");
    }

    [Fact]
    public void Capture_BlankGraphNodeId_ReportsNestedPath()
    {
        var graph = new ReasonletGraph { Nodes = [new ReasonletNode { Id = " ", Type = "step" }], Edges = [], SchemaVersion = 1 };
        AssertInvalid((TestData.Capture() with { Graph = graph }).Validate(), "Graph.Nodes[0].Id");
    }

    [Fact]
    public void Capture_NullGraph_ReportsGraph() =>
        AssertInvalid((TestData.Capture() with { Graph = null! }).Validate(), "Graph");

    [Fact]
    public void Capture_NullProvenance_ReportsProvenance() =>
        AssertInvalid((TestData.Capture() with { Provenance = null! }).Validate(), "Provenance");

    [Fact]
    public void Capture_BlankIntent_Fails() =>
        AssertInvalid((TestData.Capture() with { Intent = " " }).Validate(), "Intent");

    [Fact]
    public void Capture_NullIntent_Passes() =>
        Assert.True((TestData.Capture() with { Intent = null }).Validate().IsSuccess);

    [Fact]
    public void Capture_BlankName_Fails() =>
        AssertInvalid((TestData.Capture() with { Name = "" }).Validate(), "Name");

    [Fact]
    public void Capture_DefaultParentId_Fails() =>
        AssertInvalid((TestData.Capture() with { ParentId = default(ReasonletId) }).Validate(), "ParentId");

    [Fact]
    public void Capture_ValidParentId_Passes() =>
        Assert.True((TestData.Capture() with { ParentId = ReasonletId.New() }).Validate().IsSuccess);

    [Fact]
    public void Capture_ReportsFirstErrorOnly() =>
        AssertInvalid(
            (TestData.Capture() with { Question = "", Outcome = new ReasonletOutcome(true, TimeSpan.Zero, -1) }).Validate(),
            "Question");

    [Fact]
    public void Capture_DefaultScope_Fails() =>
        AssertInvalid((TestData.Capture() with { Scope = default }).Validate(), "Scope.Value");

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Query_TopOutOfRange_Fails(int top) =>
        AssertInvalid((TestData.Query() with { Top = top }).Validate(), "Top");

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void Query_TopInRange_Passes(int top) =>
        Assert.True((TestData.Query() with { Top = top }).Validate().IsSuccess);

    [Fact]
    public void Query_TopDefaultsToThree() =>
        Assert.Equal(3, new ReasonletQuery { Scope = new ReasonletScope("s"), Question = "q" }.Top);

    [Fact]
    public void Query_BlankQuestion_Fails() =>
        AssertInvalid((TestData.Query() with { Question = " " }).Validate(), "Question");

    [Fact]
    public void Query_BlankIntent_Fails() =>
        AssertInvalid((TestData.Query() with { Intent = "" }).Validate(), "Intent");

    [Fact]
    public void Query_DefaultScope_Fails() =>
        AssertInvalid((TestData.Query() with { Scope = default }).Validate(), "Scope.Value");

    [Fact]
    public void Query_Valid_Passes() => Assert.True(TestData.Query().Validate().IsSuccess);

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Query_MinSimilarityOutsideTheUnitInterval_Fails(double value) =>
        AssertInvalid((TestData.Query() with { MinSimilarity = value }).Validate(), "MinSimilarity");

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Query_MinSimilarityAbsentOrOnTheBoundary_Passes(double? value) =>
        Assert.True((TestData.Query() with { MinSimilarity = value }).Validate().IsSuccess);

    [Fact]
    public void Erasure_BlankSubject_Fails() =>
        AssertInvalid(new ReasonletErasure(new ReasonletScope("s"), "").Validate(), "SubjectId");

    [Fact]
    public void Erasure_NullSubject_Passes() =>
        Assert.True(new ReasonletErasure(new ReasonletScope("s"), null).Validate().IsSuccess);

    [Fact]
    public void Erasure_DefaultScope_Fails() =>
        AssertInvalid(new ReasonletErasure(default, null).Validate(), "Scope.Value");

    [Fact]
    public void Receipt_CarriesValues()
    {
        var id = ReasonletId.New();
        var exec = ExecutionId.New();
        var r = new ReasonletReceipt(id, "1.0.0", exec, ReasonletCaptureKind.NewVersion, ReasonletStatus.Active);
        Assert.Equal(id, r.Id);
        Assert.Equal("1.0.0", r.Version);
        Assert.Equal(exec, r.Execution);
        Assert.Equal(ReasonletCaptureKind.NewVersion, r.Kind);
        Assert.Equal(ReasonletStatus.Active, r.Status);
    }
}
