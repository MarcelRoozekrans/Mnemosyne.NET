using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Mnemosyne.Samples;
using Mnemosyne.Samples.Evaluation;
using Npgsql;
using ZeroAlloc.Results;

// Judge runs offline and watch the verdicts move the ranking. A rule-based evaluator checks each answer, so a run counts
// as the evaluator judged it rather than as the agent reported it, and a person can overrule the evaluator afterwards.
// The output reads as a walkthrough in three acts. The sample checks its own receipts, order and totals and exits 1 if
// they differ, so what the docs say about evaluation is tested in CI.
//
// Needs PostgreSQL 15 or later with pgvector 0.8.0 or later: `docker compose up -d --wait` next to this file starts
// one. MNEMOSYNE_SAMPLE_PG overrides the connection string. The sample migrates the mnemosyne schema with 256
// dimensions, so point it at a database of its own.
const string DefaultConnectionString = "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=mnemosyne_evaluation_sample";
const string RecallQuestion = "How do I refund an order?";

// Answers are described in words when printed; they are never echoed. The evaluator never prints them either.
const string ThroughApiQuestion = "How do I refund an order programmatically?";
const string PassingAnswer = "Refund REF-4821 was issued and the customer was notified by email.";
const string MissingNotifiedAnswer = "Refund REF-4821 was issued.";

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
    // 1. The host wiring: a data source, the PostgreSQL store, an embedding generator, an evaluator and the facade. The
    // evaluator is registered before AddMnemosyne, which then judges every run that arrives with a question and an answer.
    var services = new ServiceCollection();
    services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // a factory, so the container disposes it
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = HashingEmbeddingGenerator.Dimensions });
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
    services.AddSingleton<IReasonletEvaluator, ChecklistEvaluator>();
    services.AddMnemosyne();
    await using var provider = services.BuildServiceProvider();
    Console.WriteLine($"Wired the PostgreSQL store, the hashing embedding generator ({HashingEmbeddingGenerator.Dimensions} dimensions), the \"{ChecklistEvaluator.Id}\" evaluator and the Mnemosyne facade.");

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
        // 4. Erase the scope, whatever happened above, so a run leaves no Reasonlets behind. A failed erase is
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
    var provenance = new ReasonletProvenance { Source = "evaluation-sample" };

    Console.WriteLine();
    Console.WriteLine($"The \"{ChecklistEvaluator.Id}\" rule: an answer works when it contains a refund reference like REF-4821 and the word \"notified\".");
    Console.WriteLine("It judges the answer alone and ignores whether the agent reported success.");
    Console.WriteLine();

    // 2. Remember two procedures for near-identical questions, so similarity is close and the success rate decides.
    // A is remembered and run without an answer: nothing is judged, so each run carries only the agent's verdict.
    var byHand = Require(
        await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Name = "refund by hand",
                Question = "How do I refund an order manually?",
                Graph = Graph(("find-order", "orders.get"), ("refund", "payments.refund-manual")),
                Outcome = success,
                Provenance = provenance,
            },
            ct),
        "Remember refund by hand");
    RequireVerdict(byHand.Source, byHand.Success, ReasonletVerdictSource.Agent, true, "Remember refund by hand");
    await RecordRuns(memory, scope, byHand.Id, success, question: null, answer: null, count: 7, provenance, ct);
    Console.WriteLine("Remembered \"refund by hand\" and recorded 7 more runs, none with an answer: 8 runs the agent reported as successes.");

    // B is remembered and run with an answer that meets the rule: the evaluator judges every one of them.
    var throughApi = Require(
        await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Name = "refund through the API",
                Question = ThroughApiQuestion,
                Answer = PassingAnswer,
                Graph = Graph(("find-order", "orders.get"), ("refund", "payments.refund"), ("notify", "email.send")),
                Outcome = success,
                Provenance = provenance,
            },
            ct),
        "Remember refund through the API");
    RequireVerdict(throughApi.Source, throughApi.Success, ReasonletVerdictSource.Evaluator, true, "Remember refund through the API");
    await RecordRuns(memory, scope, throughApi.Id, success, ThroughApiQuestion, PassingAnswer, count: 4, provenance, ct);
    Console.WriteLine("Remembered \"refund through the API\" and recorded 4 more runs, all with an answer that meets the rule: 5 runs the evaluator judged as successes.");
    Console.WriteLine();

    // 3. Three acts. Each recalls the same question and checks the order and the totals the docs publish. Freshness is
    // 1.000 within a run, so the totals are stable. A mismatch is collected, not thrown, so one run reports them all.
    var mismatches = new List<string>();

    Console.WriteLine("Act 1. Every answer for \"refund through the API\" meets the rule, so its runs count in full.");
    var first = await RecallAndPrint(memory, scope, ct);
    Check("act 1", first, [throughApi.Id, byHand.Id], [0.888, 0.880], mismatches);
    Console.WriteLine();

    // The agent says this run worked, but the answer lacks the word "notified". The evaluator overrules the agent.
    Console.WriteLine("Act 2. One more run on \"refund through the API\": the agent reports success, and the answer has a refund reference but does not say the customer was notified.");
    var disputed = Require(
        await memory.RecordExecutionAsync(
            new ReasonletRun
            {
                Scope = scope,
                Id = throughApi.Id,
                Outcome = success,
                Provenance = provenance,
                Question = ThroughApiQuestion,
                Answer = MissingNotifiedAnswer,
            },
            ct),
        "Record the disputed run");
    RequireVerdict(disputed.Source, disputed.Success, ReasonletVerdictSource.Evaluator, false, "Record the disputed run");
    Console.WriteLine($"  Receipt: source {disputed.Source}, success {disputed.Success}, execution {disputed.Execution.Value}. The agent said true; the evaluator said false.");
    var second = await RecallAndPrint(memory, scope, ct);
    Check("act 2", second, [byHand.Id, throughApi.Id], [0.880, 0.867], mismatches);
    Console.WriteLine();

    // A person checks and says the customer was in fact notified. Feedback with Worked set overrides the evaluator.
    Console.WriteLine("Act 3. A person checks and says the customer was in fact notified: feedback with Worked true on that run overrides the evaluator.");
    RequireSuccess(
        await memory.RecordFeedbackAsync(scope, disputed.Execution, new ReasonletFeedback(1, 1, 1, Worked: true), ct),
        "Record the feedback");
    var third = await RecallAndPrint(memory, scope, ct);
    Check("act 3", third, [throughApi.Id, byHand.Id], [0.895, 0.880], mismatches);

    if (mismatches.Count > 0)
    {
        Console.Error.WriteLine("The recalls differ from what the sample expects.");
        foreach (var mismatch in mismatches)
        {
            Console.Error.WriteLine($"  {mismatch}");
        }

        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("Checked every receipt, the order after each act and the totals the docs publish.");
    return 0;
}

static async Task RecordRuns(
    IReasonletMemory memory, ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, string? question, string? answer, int count,
    ReasonletProvenance provenance, CancellationToken ct)
{
    var run = new ReasonletRun { Scope = scope, Id = id, Outcome = outcome, Provenance = provenance, Question = question, Answer = answer };
    var expected = answer is null ? ReasonletVerdictSource.Agent : ReasonletVerdictSource.Evaluator;
    for (var i = 0; i < count; i++)
    {
        var receipt = Require(await memory.RecordExecutionAsync(run, ct), "Record an execution");
        RequireVerdict(receipt.Source, receipt.Success, expected, true, "Record an execution");
    }
}

static async Task<IReadOnlyList<ReasonletMatch>> RecallAndPrint(IReasonletMemory memory, ReasonletScope scope, CancellationToken ct)
{
    var matches = Require(await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = RecallQuestion, Top = 3 }, ct), "Recall");
    Console.WriteLine($"  Recalled \"{RecallQuestion}\" and got {matches.Count} matches, best first:");
    Console.WriteLine($"  {"#",-2} {"Reasonlet",-24} {"Similarity",10} {"Success",8} {"Total",7} {"Runs",5} {"Judged",7}");
    for (var i = 0; i < matches.Count; i++)
    {
        var (reasonlet, score, evidence) = matches[i];
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {i + 1,-2} {reasonlet.Name,-24} {score.Similarity,10:F3} {score.SuccessRate,8:F3} {score.Total,7:F3} {evidence.Runs,5} {evidence.JudgedRuns,7}"));
    }

    return matches;
}

// Adds a line to mismatches for each way the recall differs: its order, or a total rounded to three decimals.
static void Check(string act, IReadOnlyList<ReasonletMatch> matches, ReasonletId[] expectedOrder, double[] expectedTotals, List<string> mismatches)
{
    var actualOrder = matches.Select(m => m.Reasonlet.Id).ToArray();
    var actualTotals = matches.Select(m => Math.Round(m.Score.Total, 3)).ToArray();
    if (!actualOrder.SequenceEqual(expectedOrder))
    {
        var names = string.Join(", ", matches.Select(m => m.Reasonlet.Name));
        mismatches.Add($"{act}: the order is wrong, got: {names}");
    }

    if (!actualTotals.SequenceEqual(expectedTotals))
    {
        mismatches.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{act}: expected totals {string.Join(", ", expectedTotals.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)))}, got {string.Join(", ", actualTotals.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)))}"));
    }
}

static void RequireVerdict(ReasonletVerdictSource source, bool success, ReasonletVerdictSource expectedSource, bool expectedSuccess, string what)
{
    if (source != expectedSource || success != expectedSuccess)
    {
        throw new SampleFailure($"{what} returned the verdict {source} {success}, not {expectedSource} {expectedSuccess}.");
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
