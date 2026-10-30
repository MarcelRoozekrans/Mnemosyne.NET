using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Minos;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Minos;
using Mnemosyne.NET.PostgreSql;
using Npgsql;
using ZeroAlloc.Results;

// The publish proves the packages compile clean under native AOT. This round-trip proves the source-generated
// graph serializer also works at runtime, and the hash check proves the canonical hash matches the JIT result.
// The CI job runs the binary and fails on a non-zero exit code.
const string Graph =
    """{"nodes":[{"id":"a","type":"tool","name":"Search <é>","tool":"search","properties":{"tool.args":{"q":["x",1,2.5,true,null]}}},{"id":"b","type":"answer"}],"edges":[{"from":"a","to":"b","condition":"found"}],"schemaVersion":1}""";

var read = ReasonletGraphJson.Deserialize(Graph);
if (read.IsFailure)
{
    Console.Error.WriteLine($"Deserialize failed: {read.Error.Message}");
    return 1;
}

var written = ReasonletGraphJson.Serialize(read.Value);
if (!string.Equals(written, Graph, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Round-trip differs:{Environment.NewLine}expected {Graph}{Environment.NewLine}actual   {written}");
    return 2;
}

// The SHA-256 of the canonical form of Graph; that form is a permanent contract, so this never changes.
const string ExpectedHash = "eb0d2c57c5c061d06ae9ef389db2469eea3658a4c0b28dad31918cbfac84e448";

var hash = read.Value.ComputeContentHash();
if (hash.IsFailure || !string.Equals(hash.Value, ExpectedHash, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Content hash differs:{Environment.NewLine}expected {ExpectedHash}{Environment.NewLine}actual   {(hash.IsSuccess ? hash.Value : hash.Error.Message)}");
    return 3;
}

// A transcript's tool shape must hash the same under native AOT. The captured shape is a contract like the canonical
// form: a change to it makes every rerun of a stored procedure a new version, so this constant never changes either.
const string ExpectedTranscriptHash = "6b621301322e2bcf9526794dd7f00a5161c85c7035ad235409495c6c30c092f3";

ChatMessage[] transcript =
[
    new(ChatRole.User, "Refund order 7"),
    new(ChatRole.Assistant, [new FunctionCallContent("c1", "orders.get", new Dictionary<string, object?> { ["id"] = 7 })]),
    new(ChatRole.Tool, [new FunctionResultContent("c1", "found")]),
    new(ChatRole.Assistant, [new FunctionCallContent("c2", "payments.refund", new Dictionary<string, object?> { ["orderId"] = 7, ["amount"] = 20 })]),
    new(ChatRole.Tool, [new FunctionResultContent("c2", "refunded")]),
];
var shape = ReasonletTranscript.ToGraph(transcript);
var shapeHash = shape.IsSuccess ? shape.Value.ComputeContentHash() : default;
if (shape.IsFailure || shapeHash.IsFailure || !string.Equals(shapeHash.Value, ExpectedTranscriptHash, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Transcript hash differs:{Environment.NewLine}expected {ExpectedTranscriptHash}{Environment.NewLine}actual   {(shape.IsFailure ? shape.Error.ToString() : shapeHash.IsSuccess ? shapeHash.Value : shapeHash.Error.Message)}");
    return 4;
}

var empty = ReasonletTranscript.ToGraph([new ChatMessage(ChatRole.User, "Refund order 7")]);
if (empty.IsSuccess || empty.Error != TranscriptSkipReason.NoToolCalls)
{
    Console.Error.WriteLine("A transcript without tool calls was not skipped as NoToolCalls.");
    return 5;
}

// A capture guard as a host wires it: registered in the container ahead of AddMnemosyne, resolved under native AOT. The
// store here fails the smoke on any call, so a Blocked answer also proves the guard ran before anything was stored.
var guarded = await GuardSmoke(read.Value);
if (guarded != 0)
{
    return guarded;
}

// The PostgreSQL half needs PostgreSQL 15 or later with pgvector 0.8.0 or later; CI starts one and sets
// MNEMOSYNE_AOT_PG. A slim data source has no reflection-based type mapping, so EnableArrays() is what lets the
// float[] embedding and the Guid[] parameters and columns work under native AOT. Each run works in a scope of its own
// and erases it at the end, but it leaves the migrated mnemosyne schema, its settings and the __zaorm_migrations
// history behind: point MNEMOSYNE_AOT_PG at a throwaway database.
var connectionString = Environment.GetEnvironmentVariable("MNEMOSYNE_AOT_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("MNEMOSYNE_AOT_PG is not set; skipped the PostgreSQL part of the smoke.");
    return 0;
}

try
{
    return await PostgreSqlSmoke(connectionString, read.Value);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"PostgreSQL smoke threw: {ex}");
    return 10;
}

static async Task<int> PostgreSqlSmoke(string connectionString, ReasonletGraph graph)
{
    // Listeners attach before the store runs, so the instrumentation executes under native AOT. Only counts are kept.
    var activityNames = new System.Collections.Concurrent.ConcurrentBag<string>();
    var verdictSources = new System.Collections.Concurrent.ConcurrentBag<string>();
    var measurements = 0;
    using var activityListener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == MnemosyneTelemetry.ActivitySourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity =>
        {
            activityNames.Add(activity.OperationName);
            if (activity.GetTagItem("mnemosyne.verdict_source") is string source)
            {
                verdictSources.Add($"{activity.OperationName}={source}");
            }
        },
    };
    ActivitySource.AddActivityListener(activityListener);
    using var meterListener = new MeterListener
    {
        InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MnemosyneTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        },
    };
    meterListener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
    meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurements));
    meterListener.Start();

    await using var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var ct = cts.Token;

    var migrated = await store.MigrateAsync(ct);
    if (migrated.IsFailure)
    {
        Console.Error.WriteLine($"Migrate failed: {migrated.Error.Message}");
        return 11;
    }

    var scope = new ReasonletScope($"aot-smoke-{Guid.NewGuid():N}");
    var outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), 0.5);
    var embedding = new float[] { 1f, 0f, 0f };
    var entry = new ReasonletStoreEntry(
        new ReasonletCapture
        {
            Scope = scope,
            Question = "How do I search?",
            Graph = graph,
            Outcome = outcome,
            Provenance = new ReasonletProvenance { Source = "aot-smoke", SubjectId = "smoke-subject" },
        },
        "aot-smoke",
        "AOT smoke",
        embedding);

    var remembered = await store.RememberAsync(entry, ct);
    if (remembered.IsFailure)
    {
        Console.Error.WriteLine($"Remember failed: {remembered.Error.Message}");
        return 12;
    }

    var found = await store.SearchAsync(scope, embedding, null, 5, ct);
    if (found.IsFailure || found.Value.Count != 1 || found.Value[0].Reasonlet.Id != remembered.Value.Id || found.Value[0].Metrics.Runs != 1)
    {
        Console.Error.WriteLine($"Search did not return the remembered Reasonlet: {(found.IsFailure ? found.Error.Message : $"{found.Value.Count} results")}");
        return 13;
    }

    var feedback = await store.AppendFeedbackAsync(scope, remembered.Value.Execution, new ReasonletFeedback(0.9, 0.8, 0.7), ct);
    if (feedback.IsFailure)
    {
        Console.Error.WriteLine($"Feedback failed: {feedback.Error.Message}");
        return 14;
    }

    // Three new versions down one lineage: the walk up reads a list of links, the great-grandchild's remember locks
    // the ancestor between its parent and the top with a Guid[] parameter, and the child carries tags.
    var tags = new Dictionary<string, string> { ["model"] = "smoke", ["é"] = "ü" };
    var parent = remembered.Value.Id;
    var versions = new List<ReasonletReceipt>();
    foreach (var name in new[] { "child", "grandchild", "great-grandchild" })
    {
        var version = await store.RememberAsync(
            entry with
            {
                Capture = entry.Capture with
                {
                    Graph = graph with { Nodes = [.. graph.Nodes.Select(n => n with { Name = name })] },
                    ParentId = parent,
                    Provenance = new ReasonletProvenance { Source = "aot-smoke", SubjectId = "smoke-subject", Tags = tags },
                },
            },
            ct);
        if (version.IsFailure || version.Value.Kind != ReasonletCaptureKind.NewVersion)
        {
            Console.Error.WriteLine($"New version {name} failed: {(version.IsFailure ? version.Error.Message : version.Value.Kind.ToString())}");
            return 16;
        }

        versions.Add(version.Value);
        parent = version.Value.Id;
    }

    if (!versions.Select(v => v.Version).SequenceEqual(["1.1", "1.2", "1.3"]))
    {
        Console.Error.WriteLine($"New versions were numbered {string.Join(", ", versions.Select(v => v.Version))}");
        return 17;
    }

    var child = await store.GetAsync(scope, versions[0].Id, ct);
    if (child.IsFailure
        || child.Value.ParentId != remembered.Value.Id
        || child.Value.Provenance.Tags is not { Count: 2 } read
        || read["model"] != "smoke"
        || read["é"] != "ü")
    {
        Console.Error.WriteLine($"Get did not return the child with its tags: {(child.IsFailure ? child.Error.Message : "fields differ")}");
        return 18;
    }

    // An erase by subject that keeps the Reasonlet it removes a run from: its arrays go both ways, and the kept
    // Reasonlet's metrics are recounted.
    var run = await store.AppendExecutionAsync(
        scope, remembered.Value.Id, outcome, new ReasonletProvenance { Source = "aot-smoke", SubjectId = "smoke-erased" }, null, ct);
    var bySubject = await store.EraseAsync(new ReasonletErasure(scope, "smoke-erased"), ct);
    if (run.IsFailure || run.Value.Source != ReasonletVerdictSource.Agent
        || bySubject.IsFailure || bySubject.Value.Reasonlets != 0 || bySubject.Value.Executions != 1)
    {
        Console.Error.WriteLine($"Subject erase failed: {(bySubject.IsFailure ? bySubject.Error.Message : $"{bySubject.Value.Reasonlets} Reasonlets, {bySubject.Value.Executions} executions")}");
        return 19;
    }

    var erased = await store.EraseAsync(new ReasonletErasure(scope, null), ct);
    if (erased.IsFailure || erased.Value.Reasonlets != 4 || erased.Value.Executions != 4)
    {
        Console.Error.WriteLine($"Erase failed: {(erased.IsFailure ? erased.Error.Message : $"{erased.Value.Reasonlets} Reasonlets, {erased.Value.Executions} executions deleted")}");
        return 15;
    }

    // Nothing in the scope is older than a day, so neither window deletes anything. The scope is gone already, so this
    // proves the statements run under AOT rather than what they delete.
    foreach (var retention in new[]
    {
        new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.FromDays(1) },
        new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = TimeSpan.FromDays(1) },
    })
    {
        var pruned = await store.PruneAsync(retention, ct);
        if (pruned.IsFailure || pruned.Value.Reasonlets != 0 || pruned.Value.Executions != 0)
        {
            Console.Error.WriteLine($"Prune failed: {(pruned.IsFailure ? pruned.Error.Message : $"{pruned.Value.Reasonlets} Reasonlets, {pruned.Value.Executions} executions deleted")}");
            return 20;
        }
    }

    var facade = await FacadeSmoke(dataSource, graph, ct);
    if (facade != 0)
    {
        return facade;
    }

    var gate = await GateSmoke(dataSource, graph, ct);
    if (gate != 0)
    {
        return gate;
    }

    var minos = await MinosSmoke(dataSource, graph, ct);
    if (minos != 0)
    {
        return minos;
    }

    var chat = await ChatSmoke(dataSource, graph, ct);
    if (chat != 0)
    {
        return chat;
    }

    // Every operation above stopped an activity, and the prune and erase among them: check the names, not just a count.
    string[] expected = ["mnemosyne.migrate", "mnemosyne.remember", "mnemosyne.search", "mnemosyne.erase", "mnemosyne.prune"];
    var missing = expected.Where(name => !activityNames.Contains(name)).ToArray();
    if (missing.Length > 0)
    {
        Console.Error.WriteLine($"No activity was captured for: {string.Join(", ", missing)}");
        return 21;
    }

    if (!activityNames.Contains("mnemosyne.recall"))
    {
        Console.Error.WriteLine("No activity was captured for: mnemosyne.recall");
        return 27;
    }

    if (!activityNames.Contains("mnemosyne.evaluate"))
    {
        Console.Error.WriteLine("No activity was captured for: mnemosyne.evaluate");
        return 32;
    }

    // The judged run and the human verdict on it, as the store tagged them.
    string[] expectedSources = ["mnemosyne.execution.append=evaluator", "mnemosyne.feedback.append=feedback"];
    var missingSources = expectedSources.Where(source => !verdictSources.Contains(source)).ToArray();
    if (missingSources.Length > 0)
    {
        Console.Error.WriteLine($"No verdict source tag was captured for: {string.Join(", ", missingSources)}");
        return 33;
    }

    if (Volatile.Read(ref measurements) == 0)
    {
        Console.Error.WriteLine("No metric measurement was captured.");
        return 22;
    }

    Console.WriteLine($"PostgreSQL smoke passed ({activityNames.Count} activities, {measurements} measurements captured).");
    return 0;
}

// The facade as a host wires it: the store from AddMnemosynePostgreSql, an embedding generator, and AddMnemosyne, all
// resolved from a container under native AOT, with an evaluator. It remembers, recalls the same question, records a
// judged run and a human verdict on it, and erases its own scope.
static async Task<int> FacadeSmoke(NpgsqlDataSource dataSource, ReasonletGraph graph, CancellationToken ct)
{
    var services = new ServiceCollection();
    services.AddSingleton(dataSource);
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FixedEmbeddingGenerator());
    services.AddSingleton<IReasonletEvaluator>(new ApprovingEvaluator());
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 3 });
    services.AddMnemosyne();
    await using var provider = services.BuildServiceProvider();

    var migrated = await provider.GetRequiredService<ReasonletPostgreSqlStore>().MigrateAsync(ct);
    if (migrated.IsFailure)
    {
        Console.Error.WriteLine($"Migrate through the container failed: {migrated.Error.Message}");
        return 23;
    }

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var scope = new ReasonletScope($"aot-facade-{Guid.NewGuid():N}");
    const string question = "How do I search through the facade?";
    var remembered = await memory.RememberAsync(
        new ReasonletCapture
        {
            Scope = scope,
            Question = question,
            Graph = graph,
            Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
        },
        ct);
    if (remembered.IsFailure
        || remembered.Value.Kind != ReasonletCaptureKind.NewReasonlet
        || remembered.Value.Source != ReasonletVerdictSource.Agent
        || !remembered.Value.Success)
    {
        Console.Error.WriteLine($"Remember through the facade failed: {(remembered.IsFailure ? remembered.Error.Message : remembered.Value.ToString())}");
        return 24;
    }

    var recalled = await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = question }, ct);
    if (recalled.IsFailure || recalled.Value.Count != 1 || recalled.Value[0].Reasonlet.Id != remembered.Value.Id)
    {
        Console.Error.WriteLine($"Recall did not return the remembered Reasonlet: {(recalled.IsFailure ? recalled.Error.Message : $"{recalled.Value.Count} matches")}");
        return 25;
    }

    // The evidence travels with every match: the remember is one run, judged by the agent alone, and there is no parent.
    if (recalled.Value[0].Evidence.Runs < 1
        || recalled.Value[0].Evidence != new ReasonletEvidence(1, 0, false))
    {
        Console.Error.WriteLine($"Recall carried the wrong evidence: {recalled.Value[0].Evidence}");
        return 25;
    }

    // The agent says the run failed and the judge says it worked, so the judge's verdict counts, and the receipt says so.
    // The answer is never stored, and the store reads no verdict back, so the smoke checks the stored columns in SQL.
    var judged = await memory.RecordExecutionAsync(
        new ReasonletRun
        {
            Scope = scope,
            Id = remembered.Value.Id,
            Outcome = new ReasonletOutcome(false, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
            Question = question,
            Answer = "Search with the search tool.",
        },
        ct);
    if (judged.IsFailure || judged.Value.Source != ReasonletVerdictSource.Evaluator || !judged.Value.Success)
    {
        Console.Error.WriteLine($"Recording a judged run failed: {(judged.IsFailure ? judged.Error.Message : judged.Value.ToString())}");
        return 28;
    }

    var verdict = await ReadVerdictAsync(dataSource, judged.Value.Execution, ct);
    if (verdict != (false, true, ApprovingEvaluator.Name, 0.75, null, true))
    {
        Console.Error.WriteLine($"The judged run stored {verdict}");
        return 29;
    }

    // A human's verdict overrides the judge's.
    var feedback = await memory.RecordFeedbackAsync(scope, judged.Value.Execution, new ReasonletFeedback(0.2, 0.2, 0.2, Worked: false), ct);
    if (feedback.IsFailure)
    {
        Console.Error.WriteLine($"Feedback with a verdict failed: {feedback.Error.Message}");
        return 30;
    }

    verdict = await ReadVerdictAsync(dataSource, judged.Value.Execution, ct);
    if (verdict != (false, true, ApprovingEvaluator.Name, 0.75, false, false))
    {
        Console.Error.WriteLine($"The run after feedback stored {verdict}");
        return 31;
    }

    var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), ct);
    if (erased.IsFailure || erased.Value.Reasonlets != 1 || erased.Value.Executions != 2)
    {
        Console.Error.WriteLine($"Erase through the facade failed: {(erased.IsFailure ? erased.Error.Message : $"{erased.Value.Reasonlets} Reasonlets, {erased.Value.Executions} executions deleted")}");
        return 26;
    }

    // A second facade over the same store with a guard that blocks the question: Blocked, and no row in the scope.
    var guardedServices = new ServiceCollection();
    guardedServices.AddSingleton(dataSource);
    guardedServices.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FixedEmbeddingGenerator());
    guardedServices.AddSingleton<ICaptureGuard>(new BlockingGuard());
    guardedServices.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 3 });
    guardedServices.AddMnemosyne();
    await using var guardedProvider = guardedServices.BuildServiceProvider();
    var guardedMemory = guardedProvider.GetRequiredService<IReasonletMemory>();
    var guardedScope = new ReasonletScope($"aot-guard-{Guid.NewGuid():N}");
    var blocked = await guardedMemory.RememberAsync(
        new ReasonletCapture
        {
            Scope = guardedScope,
            Question = question,
            Graph = graph,
            Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
        },
        ct);
    if (blocked.IsSuccess || blocked.Error.Code != MnemosyneErrorCode.Blocked)
    {
        Console.Error.WriteLine($"A guarded remember was not blocked: {(blocked.IsFailure ? blocked.Error.Code.ToString() : blocked.Value.ToString())}");
        return 40;
    }

    var left = await guardedMemory.EraseAsync(new ReasonletErasure(guardedScope, null), ct);
    if (left.IsFailure || left.Value.Reasonlets != 0 || left.Value.Executions != 0)
    {
        Console.Error.WriteLine($"A blocked capture left rows behind: {(left.IsFailure ? left.Error.Message : $"{left.Value.Reasonlets} Reasonlets, {left.Value.Executions} executions")}");
        return 41;
    }

    return 0;
}

// The review gate as a host wires it: RequireReview on, resolved from a container under native AOT. A new Reasonlet is
// stored pending and invisible to recall until a reviewer approves it; a rejected one is deleted. The second capture
// carries a different graph, so it is a Reasonlet of its own rather than a run of the first.
static async Task<int> GateSmoke(NpgsqlDataSource dataSource, ReasonletGraph graph, CancellationToken ct)
{
    var services = new ServiceCollection();
    services.AddSingleton(dataSource);
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FixedEmbeddingGenerator());
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 3 });
    services.AddMnemosyne(new MnemosyneOptions { RequireReview = true });
    await using var provider = services.BuildServiceProvider();

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var scope = new ReasonletScope($"aot-gate-{Guid.NewGuid():N}");
    const string question = "How do I search through the review gate?";

    var other = ReasonletGraphJson.Deserialize(
        """{"nodes":[{"id":"a","type":"tool","name":"Lookup","tool":"lookup"},{"id":"b","type":"answer"}],"edges":[{"from":"a","to":"b"}],"schemaVersion":1}""");
    if (other.IsFailure)
    {
        Console.Error.WriteLine($"Deserialize of the second gate graph failed: {other.Error.Message}");
        return 50;
    }

    var first = await memory.RememberAsync(GateCapture(scope, question, graph), ct);
    if (first.IsFailure || first.Value.Kind != ReasonletCaptureKind.NewReasonlet || first.Value.Status != ReasonletStatus.PendingReview)
    {
        Console.Error.WriteLine($"A gated remember was not pending review: {(first.IsFailure ? first.Error.Message : first.Value.ToString())}");
        return 51;
    }

    var hidden = await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = question }, ct);
    if (hidden.IsFailure || hidden.Value.Count != 0)
    {
        Console.Error.WriteLine($"Recall returned a pending Reasonlet: {(hidden.IsFailure ? hidden.Error.Message : $"{hidden.Value.Count} matches")}");
        return 52;
    }

    var pending = await memory.ListPendingAsync(scope, 10, null, ct);
    if (pending.IsFailure || pending.Value.Count != 1 || pending.Value[0].Definition.Id != first.Value.Id)
    {
        Console.Error.WriteLine($"The pending list did not hold the Reasonlet: {(pending.IsFailure ? pending.Error.Message : $"{pending.Value.Count} items")}");
        return 53;
    }

    var approved = await memory.ApproveAsync(scope, first.Value.Id, ct);
    if (approved.IsFailure)
    {
        Console.Error.WriteLine($"Approve failed: {approved.Error.Message}");
        return 54;
    }

    var visible = await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = question }, ct);
    if (visible.IsFailure || visible.Value.Count != 1 || visible.Value[0].Reasonlet.Id != first.Value.Id)
    {
        Console.Error.WriteLine($"Recall did not return the approved Reasonlet: {(visible.IsFailure ? visible.Error.Message : $"{visible.Value.Count} matches")}");
        return 55;
    }

    var second = await memory.RememberAsync(GateCapture(scope, question, other.Value), ct);
    if (second.IsFailure || second.Value.Kind != ReasonletCaptureKind.NewReasonlet || second.Value.Status != ReasonletStatus.PendingReview)
    {
        Console.Error.WriteLine($"The second gated remember was not pending review: {(second.IsFailure ? second.Error.Message : second.Value.ToString())}");
        return 56;
    }

    var rejected = await memory.RejectAsync(scope, second.Value.Id, ct);
    if (rejected.IsFailure)
    {
        Console.Error.WriteLine($"Reject failed: {rejected.Error.Message}");
        return 57;
    }

    var left = await memory.ListPendingAsync(scope, 10, null, ct);
    if (left.IsFailure || left.Value.Count != 0)
    {
        Console.Error.WriteLine($"The pending list was not empty after reject: {(left.IsFailure ? left.Error.Message : $"{left.Value.Count} items")}");
        return 58;
    }

    var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), ct);
    if (erased.IsFailure || erased.Value.Reasonlets != 1)
    {
        Console.Error.WriteLine($"Erase after the gate failed: {(erased.IsFailure ? erased.Error.Message : $"{erased.Value.Reasonlets} Reasonlets deleted")}");
        return 59;
    }

    return 0;
}

static ReasonletCapture GateCapture(ReasonletScope scope, string question, ReasonletGraph graph) => new()
{
    Scope = scope,
    Question = question,
    Graph = graph,
    Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null),
    Provenance = new ReasonletProvenance { Source = "aot-smoke" },
};

// The guard without a database: AddMnemosyne resolves the registered guard, and a capture it blocks answers Blocked
// before the embedding generator or the store is touched. Both stubs throw, so reaching either one fails the smoke.
static async Task<int> GuardSmoke(ReasonletGraph graph)
{
    var services = new ServiceCollection();
    services.AddSingleton<IReasonletStore>(new UnreachableStore());
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new UnreachableEmbeddingGenerator());
    services.AddSingleton<ICaptureGuard>(new BlockingGuard());
    services.AddMnemosyne();
    await using var provider = services.BuildServiceProvider();

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var blocked = await memory.RememberAsync(
        new ReasonletCapture
        {
            Scope = new ReasonletScope("aot-guard"),
            Question = "How do I search past the guard?",
            Graph = graph,
            Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
        },
        CancellationToken.None);
    if (blocked.IsSuccess || blocked.Error.Code != MnemosyneErrorCode.Blocked || blocked.Error.Detail != "aot-guard/blocks-all")
    {
        Console.Error.WriteLine($"The capture guard did not block: {(blocked.IsFailure ? $"{blocked.Error.Code} {blocked.Error.Detail}" : blocked.Value.ToString())}");
        return 6;
    }

    return 0;
}

// The Minos evaluator as a host wires it: a decision client in the container, AddMnemosyneMinosEvaluator, and the
// facade. The fake client answers the judge's one question with 0.9 under native AOT, which exercises the generated
// question set and the source-generated state serializer. A run the agent called a failure is judged a success.
static async Task<int> MinosSmoke(NpgsqlDataSource dataSource, ReasonletGraph graph, CancellationToken ct)
{
    var services = new ServiceCollection();
    services.AddSingleton(dataSource);
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FixedEmbeddingGenerator());
    services.AddSingleton<IDecisionClient>(new ApprovingDecisionClient());
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 3 });
    services.AddMnemosyneMinosEvaluator();
    services.AddMnemosyne();
    await using var provider = services.BuildServiceProvider();

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var scope = new ReasonletScope($"aot-minos-{Guid.NewGuid():N}");
    const string question = "How do I search through Minos?";
    var remembered = await memory.RememberAsync(
        new ReasonletCapture
        {
            Scope = scope,
            Question = question,
            Graph = graph,
            Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
        },
        ct);
    if (remembered.IsFailure)
    {
        Console.Error.WriteLine($"Remember for the Minos smoke failed: {remembered.Error.Message}");
        return 35;
    }

    var judged = await memory.RecordExecutionAsync(
        new ReasonletRun
        {
            Scope = scope,
            Id = remembered.Value.Id,
            Outcome = new ReasonletOutcome(false, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
            Question = question,
            Answer = "Search with the search tool.",
        },
        ct);
    if (judged.IsFailure || judged.Value.Source != ReasonletVerdictSource.Evaluator || !judged.Value.Success)
    {
        Console.Error.WriteLine($"Judging a run through Minos failed: {(judged.IsFailure ? judged.Error.Message : judged.Value.ToString())}");
        return 34;
    }

    var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), ct);
    if (erased.IsFailure)
    {
        Console.Error.WriteLine($"Erase after the Minos smoke failed: {erased.Error.Message}");
        return 36;
    }

    return 0;
}

// The chat evaluator as a host wires it: a chat client in the container, AddMnemosyneChatEvaluator, and the facade.
// The fake client replies with a verdict of 0.9, which exercises the source-generated state serializer, the JSON schema
// and the strict reply parser under native AOT. A run the agent called a failure is judged a success.
static async Task<int> ChatSmoke(NpgsqlDataSource dataSource, ReasonletGraph graph, CancellationToken ct)
{
    var services = new ServiceCollection();
    services.AddSingleton(dataSource);
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FixedEmbeddingGenerator());
    services.AddSingleton<IChatClient>(new ApprovingChatClient());
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 3 });
    services.AddMnemosyneChatEvaluator();
    services.AddMnemosyne();
    await using var provider = services.BuildServiceProvider();

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var scope = new ReasonletScope($"aot-chat-{Guid.NewGuid():N}");
    const string question = "How do I search through the chat judge?";
    var remembered = await memory.RememberAsync(
        new ReasonletCapture
        {
            Scope = scope,
            Question = question,
            Graph = graph,
            Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
        },
        ct);
    if (remembered.IsFailure)
    {
        Console.Error.WriteLine($"Remember for the chat smoke failed: {remembered.Error.Message}");
        return 38;
    }

    var judged = await memory.RecordExecutionAsync(
        new ReasonletRun
        {
            Scope = scope,
            Id = remembered.Value.Id,
            Outcome = new ReasonletOutcome(false, TimeSpan.FromMilliseconds(5), null),
            Provenance = new ReasonletProvenance { Source = "aot-smoke" },
            Question = question,
            Answer = "Search with the search tool.",
        },
        ct);
    if (judged.IsFailure
        || judged.Value.Source != ReasonletVerdictSource.Evaluator
        || !judged.Value.Success)
    {
        Console.Error.WriteLine($"Judging a run through the chat evaluator failed: {(judged.IsFailure ? judged.Error.Message : judged.Value.ToString())}");
        return 37;
    }

    // The receipt carries no score, so read it back from the stored run.
    var stored = await ReadVerdictAsync(dataSource, judged.Value.Execution, ct);
    if (stored is not { Evaluator: "llm-judge", Score: 0.9, Success: true })
    {
        Console.Error.WriteLine($"The chat evaluator's verdict was not stored as expected: {stored}");
        return 37;
    }

    var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), ct);
    if (erased.IsFailure)
    {
        Console.Error.WriteLine($"Erase after the chat smoke failed: {erased.Error.Message}");
        return 39;
    }

    return 0;
}

// Reads a run's verdict inputs and its effective success with a plain reader, which needs no reflection.
static async Task<(bool Agent, bool? Evaluated, string? Evaluator, double? Score, bool? Feedback, bool Success)?> ReadVerdictAsync(
    NpgsqlDataSource dataSource, ExecutionId execution, CancellationToken ct)
{
    await using var command = dataSource.CreateCommand(
        "SELECT agent_success, evaluated_success, evaluator, evaluation_score, feedback_worked, success FROM mnemosyne.reasonlet_executions WHERE id = $1");
    command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = execution.Value });
    await using var reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
    {
        return null;
    }

    return (
        reader.GetBoolean(0),
        reader.IsDBNull(1) ? null : reader.GetBoolean(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetDouble(3),
        reader.IsDBNull(4) ? null : reader.GetBoolean(4),
        reader.GetBoolean(5));
}

/// <summary>A capture guard that blocks every capture.</summary>
internal sealed class BlockingGuard : ICaptureGuard
{
    public ValueTask<Result<CaptureVerdict, MnemosyneError>> InspectAsync(ReasonletCapture capture, CancellationToken ct) =>
        ValueTask.FromResult<Result<CaptureVerdict, MnemosyneError>>(CaptureVerdict.Block("aot-guard", "blocks-all"));
}

/// <summary>A store that throws on every call, for a path that must stop before the store.</summary>
internal sealed class UnreachableStore : IReasonletStore
{
    public int Dimensions => 3;

    public ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<Result<ReasonletRunReceipt, MnemosyneError>> AppendExecutionAsync(ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneAsync(ReasonletRetention retention, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<UnitResult<MnemosyneError>> ApproveAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<UnitResult<MnemosyneError>> RejectAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) => throw new InvalidOperationException();

    public ValueTask<Result<IReadOnlyList<ReasonletReview>, MnemosyneError>> ListPendingAsync(ReasonletScope scope, int limit, ReasonletId? after, CancellationToken ct) => throw new InvalidOperationException();
}

/// <summary>An embedding generator that throws, for a path that must stop before the embedding.</summary>
internal sealed class UnreachableEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An evaluator that judges every run a success with a score of 0.75.</summary>
internal sealed class ApprovingEvaluator : IReasonletEvaluator
{
    public const string Name = "aot-judge";

    public ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct) =>
        ValueTask.FromResult<Result<ReasonletEvaluation, MnemosyneError>>(new ReasonletEvaluation(true, 0.75, Name));
}

/// <summary>
/// A Minos decision client that answers the judge's Noul with 0.9, as Minos's testing guide describes. It echoes the
/// request's own definition, so the response matches whatever question set asked. The one answer must match the question
/// set's single Noul: a second question makes the response constructor throw, so the smoke fails loudly with exit 34.
/// </summary>
internal sealed class ApprovingDecisionClient : IDecisionClient
{
    public ValueTask<Result<DecisionResponse, DecisionError>> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<DecisionResponse, DecisionError>.Success(
            new DecisionResponse(request.Definition, [QuestionAnswer.Noul(0.9)], model: "aot-fake")));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A chat client that replies to every request with a verdict of worked at 0.9 confidence.</summary>
internal sealed class ApprovingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"worked":true,"confidence":0.9}""")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An embedding generator that embeds every text as the same 3-dimension vector.</summary>
internal sealed class FixedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1f, 0f, 0f }))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
