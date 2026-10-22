using System.Runtime.CompilerServices;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Abstractions;

public class GraphShapeTests
{
    [Theory]
    [InlineData(typeof(ReasonletNode))]
    [InlineData(typeof(ReasonletEdge))]
    [InlineData(typeof(ReasonletGraph))]
    public void GraphRecordsAreInitOnly(Type record)
    {
        var constructors = record.GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
    }

    [Theory]
    [InlineData(typeof(ReasonletGraph), "Nodes")]
    [InlineData(typeof(ReasonletGraph), "Edges")]
    [InlineData(typeof(ReasonletGraph), "SchemaVersion")]
    [InlineData(typeof(ReasonletNode), "Id")]
    [InlineData(typeof(ReasonletNode), "Type")]
    [InlineData(typeof(ReasonletEdge), "From")]
    [InlineData(typeof(ReasonletEdge), "To")]
    public void CoreMembersAreRequired(Type record, string member) =>
        Assert.NotNull(record.GetProperty(member)!.GetCustomAttributes(typeof(RequiredMemberAttribute), false).SingleOrDefault());

    [Fact]
    public void CurrentSchemaVersionIsOne() => Assert.Equal(1, ReasonletGraph.CurrentSchemaVersion);
}
