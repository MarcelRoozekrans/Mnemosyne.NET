using System.Reflection;
using System.Xml.Linq;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
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
            // Property accessors are checked by TheStoreExposesItsDimensionsReadOnly.
            var methods = contract.GetMethods().Where(m => !m.IsSpecialName).ToArray();
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
    [InlineData(typeof(ReasonletRetention))]
    [InlineData(typeof(ReasonletPruneResult))]
    public void GrowableRecordsAreInitOnly(Type record)
    {
        var constructors = record.GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
    }

    [Fact]
    public void TheStoreExposesItsDimensionsReadOnly()
    {
        var properties = typeof(IReasonletStore).GetProperties();
        var dimensions = Assert.Single(properties);
        Assert.Equal("Dimensions", dimensions.Name);
        Assert.Equal(typeof(int), dimensions.PropertyType);
        Assert.True(dimensions.CanRead);
        Assert.False(dimensions.CanWrite);
        foreach (var contract in Contracts.Where(c => c != typeof(IReasonletStore)))
        {
            Assert.Empty(contract.GetProperties());
        }
    }

    [Fact]
    public void ReasonletQueryHasNoProvenance() =>
        Assert.Null(typeof(ReasonletQuery).GetProperty("Provenance"));

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
    public void PruneAsyncReturnsAPruneResult() =>
        Assert.Equal(
            typeof(ValueTask<Result<ReasonletPruneResult, MnemosyneError>>),
            typeof(IReasonletStore).GetMethod("PruneAsync")!.ReturnType);

    [Fact]
    public void PruneResult_CarriesCounts()
    {
        var result = new ReasonletPruneResult { Reasonlets = 2, Executions = 5 };
        Assert.Equal(2, result.Reasonlets);
        Assert.Equal(5, result.Executions);

        var copy = result with { Executions = 6 };
        Assert.Equal(2, copy.Reasonlets);
        Assert.Equal(6, copy.Executions);
    }

    [Fact]
    public void CheckExposesOnlyTheHelpersOtherPackagesUse()
    {
        // Public API: a change here is a breaking or widening change to every package that extends Mnemosyne.
        var members = typeof(Check).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "All(ReadOnlySpan`1)",
                "Fail(String, String)",
                "Nested(UnitResult`1, String)",
                "NotDefault(ExecutionId, String)",
                "NotDefault(ReasonletId, String)",
                "NotNull(Object, String)",
                "Range(Int32, Int32, Int32, String)",
                "UnitInterval(Double, String)",
            ],
            members);
        Assert.True(typeof(Check).IsPublic);
        Assert.Equal("Mnemosyne.NET.Abstractions.Validation", typeof(Check).Namespace);
    }

    [Fact]
    public void OnlyTheTestProjectSeesAbstractionsInternals()
    {
        var csproj = Path.Combine(RepoConventionsTests.RepoRoot(), "src", "Mnemosyne.NET.Abstractions", "Mnemosyne.NET.Abstractions.csproj");
        var friends = XDocument.Load(csproj).Descendants("InternalsVisibleTo")
            .Select(e => e.Attribute("Include")?.Value)
            .ToArray();
        Assert.Equal(new string?[] { "Mnemosyne.NET.Tests" }, friends);
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
