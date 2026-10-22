using Xunit;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class GraphAndDefinitionTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    [Fact]
    public void Graph_Valid_Passes() => Assert.True(TestData.Graph().Validate().IsSuccess);

    [Fact]
    public void Graph_SchemaVersionZero_Fails() =>
        AssertInvalid((TestData.Graph() with { SchemaVersion = 0 }).Validate(), "SchemaVersion");

    [Fact]
    public void Graph_BlankNodeType_ReportsIndexedPath() =>
        AssertInvalid((TestData.Graph() with { Nodes = [new ReasonletNode("a", "t"), new ReasonletNode("b", " ")] }).Validate(), "Nodes[1].Type");

    [Fact]
    public void Graph_BlankEdgeEndpoint_ReportsIndexedPath() =>
        AssertInvalid((TestData.Graph() with { Edges = [new ReasonletEdge("a", "")] }).Validate(), "Edges[0].To");

    [Fact]
    public void Graph_NullNodes_FailsWithoutThrowing() =>
        AssertInvalid((TestData.Graph() with { Nodes = null! }).Validate(), "Nodes");

    [Fact]
    public void Graph_NullEdges_FailsWithoutThrowing() =>
        AssertInvalid((TestData.Graph() with { Edges = null! }).Validate(), "Edges");

    [Fact]
    public void Graph_NullNodeElement_FailsWithoutThrowing() =>
        AssertInvalid((TestData.Graph() with { Nodes = [null!] }).Validate(), "Nodes[0]");

    [Fact]
    public void Definition_Valid_Passes() => Assert.True(TestData.Definition().Validate().IsSuccess);

    [Fact]
    public void Definition_DefaultId_Fails() =>
        AssertInvalid((TestData.Definition() with { Id = default }).Validate(), "Id");

    [Fact]
    public void Definition_BlankScope_Fails() =>
        AssertInvalid((TestData.Definition() with { Scope = default }).Validate(), "Scope.Value");

    [Fact]
    public void Definition_BlankVersion_Fails() =>
        AssertInvalid((TestData.Definition() with { Version = " " }).Validate(), "Version");

    [Fact]
    public void Definition_BadGraphNode_ReportsNestedPath() =>
        AssertInvalid((TestData.Definition() with { Graph = TestData.Graph() with { Nodes = [new ReasonletNode("", "t")] } }).Validate(), "Graph.Nodes[0].Id");

    [Fact]
    public void Definition_NullGraph_Fails() =>
        AssertInvalid((TestData.Definition() with { Graph = null! }).Validate(), "Graph");

    [Fact]
    public void Definition_DefaultParentId_Fails() =>
        AssertInvalid((TestData.Definition() with { ParentId = default(ReasonletId) }).Validate(), "ParentId");

    [Fact]
    public void Definition_NullProvenance_Fails() =>
        AssertInvalid((TestData.Definition() with { Provenance = null! }).Validate(), "Provenance");

    [Fact]
    public void Definition_BadProvenance_ReportsNestedPath() =>
        AssertInvalid((TestData.Definition() with { Provenance = TestData.Provenance() with { Source = "" } }).Validate(), "Provenance.Source");

    [Fact]
    public void Candidate_Valid_Passes() =>
        Assert.True(new ReasonletCandidate(TestData.Definition(), 0.5, default).Validate().IsSuccess);

    [Fact]
    public void Candidate_SimilarityAboveOne_Fails() =>
        AssertInvalid(new ReasonletCandidate(TestData.Definition(), 1.2, default).Validate(), "Similarity");

    [Fact]
    public void Candidate_NullReasonlet_Fails() =>
        AssertInvalid(new ReasonletCandidate(null!, 0.5, default).Validate(), "Reasonlet");

    [Fact]
    public void Candidate_BadMetrics_ReportsNestedPath() =>
        AssertInvalid(new ReasonletCandidate(TestData.Definition(), 0.5, new ReasonletMetrics(-1, 0, null)).Validate(), "Metrics.Runs");

    [Fact]
    public void Match_Valid_Passes() =>
        Assert.True(new ReasonletMatch(TestData.Definition(), default).Validate().IsSuccess);

    [Fact]
    public void Match_NullReasonlet_Fails() =>
        AssertInvalid(new ReasonletMatch(null!, default).Validate(), "Reasonlet");

    [Fact]
    public void Match_BadScore_ReportsNestedPath() =>
        AssertInvalid(new ReasonletMatch(TestData.Definition(), new ReasonletScore(2, 0, 0, 0)).Validate(), "Score.Similarity");
}
