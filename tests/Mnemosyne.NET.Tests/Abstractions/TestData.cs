using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Tests.Abstractions;

/// <summary>Builders for valid instances shared across tests.</summary>
internal static class TestData
{
    public static ReasonletProvenance Provenance() => new("test", null, null, null);

    public static ReasonletGraph Graph() => new(
        [new ReasonletNode("n1", "step"), new ReasonletNode("n2", "step")],
        [new ReasonletEdge("n1", "n2")],
        1);

    public static ReasonletDefinition Definition() => new(
        ReasonletId.New(),
        new ReasonletScope("scope"),
        "name",
        "intent",
        "1.0.0",
        Graph(),
        null,
        Provenance(),
        ReasonletStatus.Active,
        DateTimeOffset.UnixEpoch);
}
