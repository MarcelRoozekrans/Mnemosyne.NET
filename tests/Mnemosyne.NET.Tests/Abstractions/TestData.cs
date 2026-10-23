using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Tests.Abstractions;

/// <summary>Builders for valid instances shared across tests.</summary>
internal static class TestData
{
    public static ReasonletProvenance Provenance() => new() { Source = "test" };

    public static ReasonletGraph Graph() => new()
    {
        Nodes = [new ReasonletNode { Id = "n1", Type = "step" }, new ReasonletNode { Id = "n2", Type = "step" }],
        Edges = [new ReasonletEdge { From = "n1", To = "n2" }],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
    };

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
    };

    public static ReasonletCapture Capture() => new()
    {
        Scope = new ReasonletScope("scope"),
        Question = "question",
        Graph = Graph(),
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), 0.5),
        Provenance = Provenance(),
    };

    public static ReasonletStoreEntry StoreEntry() => new(Capture(), "intent", "name", new[] { 0.1f, 0.2f, 0.3f });
}
