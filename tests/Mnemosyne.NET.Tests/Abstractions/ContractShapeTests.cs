using System.Reflection;
using System.Xml.Linq;
using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class ContractShapeTests
{
    private static readonly Type[] Contracts =
    [
        typeof(IReasonletMemory),
        typeof(IReasonletStore),
        typeof(IReasonletSelector),
        typeof(IIntentExtractor),
    ];

    [Fact]
    public void EveryAsyncMemberFollowsTheHouseStyle()
    {
        foreach (var contract in Contracts)
        {
            var methods = contract.GetMethods();
            Assert.NotEmpty(methods);
            foreach (var m in methods)
            {
                var label = contract.Name + "." + m.Name;
                Assert.True(m.Name.EndsWith("Async", StringComparison.Ordinal), label + " must end with Async");

                var rt = m.ReturnType;
                Assert.True(rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>), label + " must return ValueTask<>");
                var inner = rt.GetGenericArguments()[0];
                Assert.True(inner.IsGenericType, label + " must wrap a ZeroAlloc result");
                var def = inner.GetGenericTypeDefinition();
                var args = inner.GetGenericArguments();
                if (def == typeof(Result<,>))
                {
                    Assert.Equal(typeof(MnemosyneError), args[1]);
                }
                else
                {
                    Assert.Equal(typeof(UnitResult<>), def);
                    Assert.Equal(typeof(MnemosyneError), args[0]);
                }

                var parameters = m.GetParameters();
                Assert.True(parameters.Length > 0, label + " must take a CancellationToken ct");
                var last = parameters[^1];
                Assert.True(last.ParameterType == typeof(CancellationToken), label + " last parameter must be a CancellationToken");
                Assert.True(last.Name == "ct", label + " last parameter must be named ct");
                Assert.False(last.HasDefaultValue, label + " ct must have no default");
            }
        }
    }

    [Theory]
    [InlineData(typeof(ReasonletCapture))]
    [InlineData(typeof(ReasonletNode))]
    [InlineData(typeof(ReasonletEdge))]
    [InlineData(typeof(ReasonletGraph))]
    [InlineData(typeof(ReasonletDefinition))]
    [InlineData(typeof(ReasonletProvenance))]
    [InlineData(typeof(ReasonletQuery))]
    [InlineData(typeof(ReasonletErasureResult))]
    public void GrowableRecordsAreInitOnly(Type record)
    {
        var constructors = record.GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
    }

    [Theory]
    [InlineData(typeof(IReasonletMemory))]
    [InlineData(typeof(IReasonletStore))]
    public void EraseAsyncReturnsAnErasureResult(Type contract) =>
        Assert.Equal(
            typeof(ValueTask<Result<ReasonletErasureResult, MnemosyneError>>),
            contract.GetMethod("EraseAsync")!.ReturnType);

    [Fact]
    public void ErasureResult_CarriesCounts()
    {
        var result = new ReasonletErasureResult { Reasonlets = 2, Executions = 5 };
        Assert.Equal(2, result.Reasonlets);
        Assert.Equal(5, result.Executions);

        // Init-only so later phases can add members; a with-expression copies the rest.
        var copy = result with { Executions = 6 };
        Assert.Equal(2, copy.Reasonlets);
        Assert.Equal(6, copy.Executions);
    }

    [Fact]
    public void AbstractionsReferencesOnlyZeroAllocResults()
    {
        var csproj = Path.Combine(RepoConventionsTests.RepoRoot(), "src", "Mnemosyne.NET.Abstractions", "Mnemosyne.NET.Abstractions.csproj");
        var packages = XDocument.Load(csproj).Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .ToArray();
        Assert.Equal(new string?[] { "ZeroAlloc.Results" }, packages);
    }
}
