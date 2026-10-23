using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;

namespace Mnemosyne.NET.Tests.Minos;

/// <summary>Builders shared by the registration and facade tests.</summary>
internal static class MinosTestData
{
    public static ReasonletEvaluationRequest Request() => new()
    {
        Graph = TestData.Graph(),
        Name = "name",
        Intent = "intent",
        Question = "Where is invoice 42?",
        Answer = "It is in the March folder.",
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), 0.5),
    };
}
