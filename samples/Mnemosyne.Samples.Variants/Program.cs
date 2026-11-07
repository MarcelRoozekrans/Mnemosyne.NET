using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.Samples;
using static Mnemosyne.Samples.SampleRunner;

// Watch a new version of a Reasonlet overtake a proven one. The incumbent has 200 runs at a 70% success rate; its new
// version has no runs yet and would really succeed 90% of the time. The sample builds the candidates by hand, so it needs
// no database, no model and no key, and nothing it prints depends on the clock or on chance: the outcomes are drawn from
// a seeded Random and the selector gets a fixed seed.
// Three acts: ReasonletSelector alone never tries the new version; VariantSelector without exploration returns one
// variant per lineage; VariantSelector with Thompson sampling tries the new version early and then prefers it.
// The sample checks its own counts and exits 1 if they differ, so what the docs say about variants is tested in CI.
return await SampleRunner.RunAsync(Run, TimeSpan.FromMinutes(1), Console.Error);

static async Task<int> Run(CancellationToken ct)
{
    var world = new World();
    var ranking = new ReasonletRankingOptions();

    // Act 1. The plain selector ranks every candidate on its own record, so the version with no runs scores as its
    // parent does and never beats it. Without a pick it never earns a run.
    Console.WriteLine("Act 1. ReasonletSelector alone: the incumbent has 200 runs, its new version has none.");
    var plain = new ReasonletSelector(ranking, new FixedTimeProvider());
    var plainPicks = await world.Simulate(plain, ct);
    var plainChild = plainPicks.Count(c => c);
    Console.WriteLine($"  New version chosen in {plainChild} of {World.Rounds} rounds.");
    RequireEqual(plainChild, 0, "Act 1: the rounds in which the new version was chosen");

    // Act 2. VariantSelector without exploration keeps one variant per lineage and returns the unrelated procedure too.
    Console.WriteLine("Act 2. VariantSelector without exploration: one match per lineage.");
    var none = new VariantSelector(plain, new VariantSelectionOptions(), ranking);
    var matches = Require(await none.RankAsync(World.Query(0), world.Candidates(World.Incumbent, World.Child), ct), "Act 2: the ranking");
    foreach (var match in matches)
    {
        Console.WriteLine($"  {match.Reasonlet.Name}");
    }

    RequireEqual(matches.Count, 2, "Act 2: the number of matches");
    RequireEqual(matches.Count(m => m.Reasonlet.Id == World.IncumbentId || m.Reasonlet.Id == World.ChildId), 1, "Act 2: the variants of the lineage");
    RequireEqual(matches.Count(m => m.Reasonlet.Id == World.UnrelatedId), 1, "Act 2: the unrelated procedure");

    // Act 3. With Thompson sampling the selector draws a plausible success rate for each variant, and a variant with
    // little evidence has a wide range, so it gets tried. Once the new version has runs its 0.9 wins nearly every round.
    Console.WriteLine("Act 3. VariantSelector with Thompson sampling, seed 11.");
    var thompson = new VariantSelector(
        plain, new VariantSelectionOptions { Exploration = VariantExploration.Thompson, Seed = 11 }, ranking);
    var picks = await world.Simulate(thompson, ct);
    var first20 = picks.Take(20).Count(c => c);
    var last100 = picks.Skip(World.Rounds - 100).Count(c => c);
    Console.WriteLine($"  New version chosen in {first20} of the first 20 rounds and {last100} of the last 100.");
    if (first20 < 1)
    {
        throw new SampleFailure($"Act 3: the new version was chosen in {first20} of the first 20 rounds, not at least 1.");
    }

    if (last100 < 60)
    {
        throw new SampleFailure($"Act 3: the new version was chosen in {last100} of the last 100 rounds, not at least 60.");
    }

    Console.WriteLine("All checks passed.");
    return 0;
}

internal sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => World.Now;
}

internal sealed class World
{
    public const int Rounds = 300;
    private const double IncumbentRate = 0.7;
    private const double ChildRate = 0.9;

    public static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // Fixed ids, the incumbent's the lower, as time-ordered v7 ids would be for the older version. A new version with
    // no runs of its own scores exactly as its parent does, so under the default ranking the two tie on score and
    // similarity and the lowest id wins. Random ids would make that tie-break depend on the clock.
    public static readonly ReasonletId IncumbentId = new(new Guid("00000000-0000-0000-0000-000000000001"));
    public static readonly ReasonletId ChildId = new(new Guid("00000000-0000-0000-0000-000000000002"));
    public static readonly ReasonletId UnrelatedId = new(new Guid("00000000-0000-0000-0000-000000000003"));

    // A fixed lineage id too: the seeded draws hash it, so a random one would change them from run to run.
    private static readonly ReasonletId Lineage = new(new Guid("00000000-0000-0000-0000-00000000000a"));

    public static readonly ReasonletMetrics Incumbent = new(200, 140, null, Now);
    public static readonly ReasonletMetrics Child = new(0, 0, null, Now);

    // The question carries the round number: a seeded draw is fixed per seed, question and lineage, so a constant
    // question would repeat the same draw whenever the metrics repeat.
    public static ReasonletQuery Query(int round) => new()
    {
        Scope = new ReasonletScope("variants-sample"),
        Question = $"how do I rebase {round}",
        Top = 10,
    };

    public IReadOnlyList<ReasonletCandidate> Candidates(ReasonletMetrics incumbent, ReasonletMetrics child) =>
    [
        Candidate(IncumbentId, "rebase, original", Lineage, 0.8, incumbent, null),
        Candidate(ChildId, "rebase, new version", Lineage, 0.8, child, incumbent),
        Candidate(UnrelatedId, "squash merge", UnrelatedId, 0.6, new ReasonletMetrics(10, 5, null, Now), null),
    ];

    // One recall per round, the metrics updated with a seeded outcome for whichever variant was chosen.
    // Returns, per round, whether the new version was chosen.
    public async Task<bool[]> Simulate(IReasonletSelector selector, CancellationToken ct)
    {
        var outcomes = new Random(11);
        var incumbent = Incumbent;
        var child = Child;
        var chosen = new bool[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            var ranked = Require(await selector.RankAsync(Query(round), Candidates(incumbent, child), ct), $"Round {round}: the ranking");
            var pick = ranked[0].Reasonlet.Id;
            chosen[round] = pick == ChildId;
            var success = outcomes.NextDouble() < (chosen[round] ? ChildRate : IncumbentRate);
            if (chosen[round])
            {
                child = Next(child, success);
            }
            else
            {
                incumbent = Next(incumbent, success);
            }
        }

        return chosen;
    }

    private static ReasonletMetrics Next(ReasonletMetrics metrics, bool success) =>
        new(metrics.Runs + 1, metrics.Successes + (success ? 1 : 0), null, Now);

    private static ReasonletCandidate Candidate(
        ReasonletId id, string name, ReasonletId lineage, double similarity, ReasonletMetrics metrics, ReasonletMetrics? parent) => new()
    {
        LineageId = lineage,
        Similarity = similarity,
        Metrics = metrics,
        ParentMetrics = parent,
        Reasonlet = new ReasonletDefinition
        {
            Id = id,
            Scope = new ReasonletScope("variants-sample"),
            Name = name,
            Intent = name,
            Version = "1.0.0",
            Graph = new ReasonletGraph
            {
                Nodes = [new ReasonletNode { Id = "n1", Type = "step" }],
                Edges = [],
                SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
            },
            Provenance = new ReasonletProvenance { Source = "sample" },
            Status = ReasonletStatus.Active,
            CreatedAt = Now,
        },
    };
}
