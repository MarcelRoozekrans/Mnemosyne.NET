using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Mnemosyne.Samples.StoreAndRecall;
using Npgsql;
using ZeroAlloc.Results;

// Store a few procedures, record how they ran, capture a new version of the proven one, and recall a question.
// The output reads as a walkthrough. The sample checks its own ranking at the end and exits 1 if it differs, so
// what the docs say about recall is tested in CI.
//
// Needs PostgreSQL 15 or later with pgvector 0.8.0 or later: `docker compose up -d --wait` next to this file starts
// one. MNEMOSYNE_SAMPLE_PG overrides the connection string. The sample migrates the mnemosyne schema with 256
// dimensions, so point it at a database of its own.
const string DefaultConnectionString = "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=mnemosyne_sample";
const string RecallQuestion = "How do I refund an order?";

var connectionString = Environment.GetEnvironmentVariable("MNEMOSYNE_SAMPLE_PG") is { Length: > 0 } configured
    ? configured
    : DefaultConnectionString;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = cts.Token;

try
{
    return await Run(connectionString, ct);
}
catch (SampleFailure failure)
{
    Console.Error.WriteLine($"FAILED: {failure.Message}");
    return 1;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("FAILED: the sample did not finish within 2 minutes.");
    return 1;
}

static async Task<int> Run(string connectionString, CancellationToken ct)
{
    // 1. The host wiring: a data source, the PostgreSQL store, an embedding generator and the facade. The store's
    // Dimensions must match the generator's output. HashingEmbeddingGenerator is an offline stand-in for a real model.
    var services = new ServiceCollection();
    services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // a factory, so the container disposes it
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = HashingEmbeddingGenerator.Dimensions });
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
    services.AddMnemosyne();
    await using var provider = services.BuildServiceProvider();
    Console.WriteLine($"Wired the PostgreSQL store, the hashing embedding generator ({HashingEmbeddingGenerator.Dimensions} dimensions) and the Mnemosyne facade.");

    RequireSuccess(await provider.GetRequiredService<ReasonletPostgreSqlStore>().MigrateAsync(ct), "Migrate");
    Console.WriteLine("Migrated the mnemosyne schema.");

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var scope = new ReasonletScope($"sample-{Guid.NewGuid():N}");
    Console.WriteLine($"Working in scope {scope.Value}.");

    var exitCode = 1;
    try
    {
        exitCode = await Walkthrough(memory, scope, ct);
    }
    finally
    {
        // 5. Erase the scope, whatever happened above, so a run leaves no Reasonlets behind. A failed erase is
        // reported but never replaces a failure that is already on its way out.
        if (!await TryErase(memory, scope))
        {
            exitCode = 1;
        }
    }

    return exitCode;
}

// Runs in a finally block, so it must not throw: an exception here would hide the one that got the sample there.
// The erase does not take the run's token, so a timed-out run still cleans up.
static async Task<bool> TryErase(IReasonletMemory memory, ReasonletScope scope)
{
    try
    {
        var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), CancellationToken.None);
        if (erased.IsFailure)
        {
            Console.Error.WriteLine($"Erase failed: {erased.Error.Code}: {erased.Error.Message}");
            return false;
        }

        Console.WriteLine($"Erased the scope: {erased.Value.Reasonlets} Reasonlets and {erased.Value.Executions} executions deleted.");
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Erase threw {ex.GetType().Name}: {ex.Message}");
        return false;
    }
}

static async Task<int> Walkthrough(IReasonletMemory memory, ReasonletScope scope, CancellationToken ct)
{
    var success = new ReasonletOutcome(true, TimeSpan.FromSeconds(2), null);
    var failure = new ReasonletOutcome(false, TimeSpan.FromSeconds(2), null);
    var provenance = new ReasonletProvenance { Source = "store-and-recall-sample" };

    // 2. Remember three procedures. Each remember records its first run, with the capture's outcome; RecordExecutionAsync
    // records every run after that.
    var original = Require(
        await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Name = "refund an order",
                Question = "How do I refund an order for a customer?",
                Graph = Graph(("find-order", "orders.get"), ("refund", "payments.refund")),
                Outcome = success,
                Provenance = provenance,
            },
            ct),
        "Remember the original");
    await RecordRuns(memory, scope, original.Id, success, 19, provenance, ct);
    Console.WriteLine($"Remembered \"refund an order\" {original.Version} and recorded 19 more successful runs: 20 of 20.");

    var closeMatch = Require(
        await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Name = "refund a partial order",
                Question = "How do I refund part of an order?",
                Graph = Graph(("find-order", "orders.get"), ("pick-lines", "orders.lines"), ("refund-lines", "payments.refund-partial")),
                Outcome = success,
                Provenance = provenance,
            },
            ct),
        "Remember the close match");
    await RecordRuns(memory, scope, closeMatch.Id, success, 4, provenance, ct);
    await RecordRuns(memory, scope, closeMatch.Id, failure, 5, provenance, ct);
    Console.WriteLine($"Remembered \"refund a partial order\" {closeMatch.Version}, a close match, and recorded 4 successful and 5 failed runs: 5 of 10.");

    // A new version: the same question with a different graph and ParentId set. It starts from its parent's record,
    // so one successful run of its own is enough to rank above the original it refines.
    var newVersion = Require(
        await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Name = "refund an order",
                Question = "How do I refund an order for a customer?",
                Graph = Graph(("find-order", "orders.get"), ("check-window", "policy.refund-window"), ("refund", "payments.refund"), ("notify", "email.send")),
                Outcome = success,
                Provenance = provenance,
                ParentId = original.Id,
            },
            ct),
        "Remember the new version");
    if (newVersion.Kind != ReasonletCaptureKind.NewVersion)
    {
        throw new SampleFailure($"the refined graph was stored as {newVersion.Kind}, not as a new version");
    }

    Console.WriteLine($"Remembered a new version of \"refund an order\", {newVersion.Version}, with a refund-window check and a customer email: 1 of 1.");

    // 3. Recall. Ranking: 0.5 similarity + 0.3 smoothed success rate + 0.2 freshness, after a 0.5 similarity cut-off.
    var matches = Require(await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = RecallQuestion, Top = 3 }, ct), "Recall");
    Console.WriteLine($"Recalled \"{RecallQuestion}\" and got {matches.Count} matches, best first:");
    Console.WriteLine();
    Console.WriteLine($"  {"#",-2} {"Reasonlet",-24} {"Version",-8} {"Similarity",10} {"Success",8} {"Freshness",10} {"Total",7}");
    for (var i = 0; i < matches.Count; i++)
    {
        var (reasonlet, score) = matches[i];
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {i + 1,-2} {reasonlet.Name,-24} {reasonlet.Version,-8} {score.Similarity,10:F3} {score.SuccessRate,8:F3} {score.Freshness,10:F3} {score.Total,7:F3}"));
    }

    Console.WriteLine();
    if (matches.Any(m => m.Reasonlet.Id == closeMatch.Id))
    {
        Console.WriteLine("The close match scores the highest similarity, but its 5-of-10 record ranks it below both refund versions.");
    }
    else
    {
        Console.WriteLine("The close match fell below the 0.5 similarity cut-off, so recall dropped it.");
    }

    // 4. Check the ranking the docs describe: three matches, the new version first, the proven original second and
    // the close match last, with the totals the docs publish. Freshness is 1.000 within a run, so they are stable.
    ReasonletId[] expected = [newVersion.Id, original.Id, closeMatch.Id];
    double[] expectedTotals = [0.883, 0.878, 0.802];
    var actual = matches.Select(m => m.Reasonlet.Id).ToArray();
    var actualTotals = matches.Select(m => Math.Round(m.Score.Total, 3)).ToArray();
    if (matches.Count != expected.Length || !actual.SequenceEqual(expected) || !actualTotals.SequenceEqual(expectedTotals))
    {
        Console.Error.WriteLine("The ranking differs from what the sample expects.");
        Console.Error.WriteLine($"  matches: expected {expected.Length}, got {matches.Count}");
        Console.Error.WriteLine($"  expected order:  {Describe(expected, matches, original, closeMatch, newVersion)}");
        Console.Error.WriteLine($"  actual order:    {Describe(actual, matches, original, closeMatch, newVersion)}");
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  expected totals: {string.Join(", ", expectedTotals.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)))}"));
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  actual totals:   {string.Join(", ", actualTotals.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)))}"));
        return 1;
    }

    Console.WriteLine("Checked the ranking: the new version first, the proven original second and the close match last, with the totals the docs publish.");
    return 0;
}

static async Task RecordRuns(
    IReasonletMemory memory, ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, int count, ReasonletProvenance provenance, CancellationToken ct)
{
    for (var i = 0; i < count; i++)
    {
        Require(await memory.RecordExecutionAsync(scope, id, outcome, provenance, ct), "Record an execution");
    }
}

// A linear graph: each step calls one tool, and the last step leads to the answer.
static ReasonletGraph Graph(params (string Id, string Tool)[] steps)
{
    var nodes = steps.Select(s => new ReasonletNode { Id = s.Id, Type = "tool", Tool = s.Tool })
        .Append(new ReasonletNode { Id = "answer", Type = "answer" })
        .ToArray();
    var edges = nodes.Zip(nodes.Skip(1), (from, to) => new ReasonletEdge { From = from.Id, To = to.Id }).ToArray();
    return new ReasonletGraph { Nodes = nodes, Edges = edges, SchemaVersion = ReasonletGraph.CurrentSchemaVersion };
}

static string Describe(
    IEnumerable<ReasonletId> ids, IReadOnlyList<ReasonletMatch> matches, ReasonletReceipt original, ReasonletReceipt closeMatch, ReasonletReceipt newVersion) =>
    string.Join(", ", ids.Select(id =>
        id == newVersion.Id ? $"the new version {newVersion.Version}"
        : id == original.Id ? $"the original {original.Version}"
        : id == closeMatch.Id ? $"the close match {closeMatch.Version}"
        : matches.FirstOrDefault(m => m.Reasonlet.Id == id)?.Reasonlet.Name ?? id.ToString()));

static T Require<T>(Result<T, MnemosyneError> result, string what) =>
    result.IsSuccess ? result.Value : throw new SampleFailure($"{what} failed: {result.Error.Code}: {result.Error.Message}");

static void RequireSuccess(UnitResult<MnemosyneError> result, string what)
{
    if (result.IsFailure)
    {
        throw new SampleFailure($"{what} failed: {result.Error.Code}: {result.Error.Message}");
    }
}

/// <summary>A call the sample depends on failed; the message says which.</summary>
internal sealed class SampleFailure(string message) : Exception(message);
