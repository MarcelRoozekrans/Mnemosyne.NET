using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Benchmarks;

/// <summary>Deterministic data for the benchmarks: the same call always builds the same values, so runs compare.</summary>
internal static class BenchmarkData
{
    public static readonly ReasonletScope Scope = new("benchmark");

    /// <summary>The fixed instant the benchmarks treat as now, so freshness does not drift between runs.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Builds a unit-length embedding from a seed.</summary>
    public static float[] Embedding(int seed, int dimensions)
    {
        var random = new Random(seed);
        var values = new float[dimensions];
        double sum = 0;
        for (var i = 0; i < dimensions; i++)
        {
            values[i] = (float)(random.NextDouble() - 0.5);
            sum += values[i] * values[i];
        }

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < dimensions; i++)
        {
            values[i] /= norm;
        }

        return values;
    }

    /// <summary>Builds a valid definition whose id and name follow the index.</summary>
    public static ReasonletDefinition Reasonlet(int index) => new()
    {
        Id = new ReasonletId(new Guid(index + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1])),
        Scope = Scope,
        Name = $"procedure-{index}",
        Intent = "benchmark",
        Version = "1.0.0",
        Graph = new ReasonletGraph
        {
            Nodes = [new ReasonletNode { Id = "n1", Type = "step" }, new ReasonletNode { Id = "n2", Type = "step" }],
            Edges = [new ReasonletEdge { From = "n1", To = "n2" }],
            SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
        },
        Provenance = new ReasonletProvenance { Source = "benchmark" },
        Status = ReasonletStatus.Active,
        CreatedAt = Now.AddDays(-30),
    };

    /// <summary>Builds candidates with a fixed spread of similarity, runs and last-executed times.</summary>
    public static IReadOnlyList<ReasonletCandidate> Candidates(int count)
    {
        var candidates = new ReasonletCandidate[count];
        for (var i = 0; i < count; i++)
        {
            var runs = (i * 7) % 50;
            var successes = (runs * ((i % 5) + 1)) / 6;
            var definition = Reasonlet(i);
            candidates[i] = new ReasonletCandidate
            {
                LineageId = definition.Id,
                Reasonlet = definition,
                Similarity = 0.5 + (((i * 13) % 50) / 100.0),
                Metrics = new ReasonletMetrics(runs, successes, Now.AddDays(-(i % 20)), Now.AddDays(-((i * 3) % 40))),
            };
        }

        return candidates;
    }
}
