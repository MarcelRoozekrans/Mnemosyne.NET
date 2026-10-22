using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;

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
        scope, remembered.Value.Id, outcome, new ReasonletProvenance { Source = "aot-smoke", SubjectId = "smoke-erased" }, ct);
    var bySubject = await store.EraseAsync(new ReasonletErasure(scope, "smoke-erased"), ct);
    if (run.IsFailure || bySubject.IsFailure || bySubject.Value.Reasonlets != 0 || bySubject.Value.Executions != 1)
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

    Console.WriteLine("PostgreSQL smoke passed.");
    return 0;
}
