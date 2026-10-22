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
                Assert.EndsWith("Async", m.Name, StringComparison.Ordinal);

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

                var last = m.GetParameters()[^1];
                Assert.Equal(typeof(CancellationToken), last.ParameterType);
                Assert.Equal("ct", last.Name);
                Assert.False(last.HasDefaultValue, label + " ct must have no default");
            }
        }
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
