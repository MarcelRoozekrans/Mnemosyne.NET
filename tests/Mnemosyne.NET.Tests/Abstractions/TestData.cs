using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Tests.Abstractions;

/// <summary>Builders for valid instances shared across tests.</summary>
internal static class TestData
{
    public static ReasonletProvenance Provenance() => new() { Source = "test" };

    public static ReasonletGraph Graph() => new(
        [new ReasonletNode("n1", "step"), new ReasonletNode("n2", "step")],
        [new ReasonletEdge("n1", "n2")],
        1);

    public static ReasonletDefinition Definition() => new()
    {
        Id = ReasonletId.New(),
        Scope = new ReasonletScope("scope"),
        Name = "name",
        Intent = "intent",
        Version = "1.0.0",
        Graph = Graph(),
        Provenance = Provenance(),
        Status = ReasonletStatus.Active,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    public static ReasonletQuery Query() => new()
    {
        Scope = new ReasonletScope("scope"),
        Question = "question",
        Provenance = Provenance(),
    };

    public static ReasonletCapture Capture() => new()
    {
        Scope = new ReasonletScope("scope"),
        Question = "question",
        Graph = Graph(),
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), 0.5),
        Provenance = Provenance(),
    };
}
