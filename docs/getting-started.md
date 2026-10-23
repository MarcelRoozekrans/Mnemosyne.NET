---
title: Getting started
sidebar_position: 2
---

# Getting started

:::note
The packages are published with the first release, 0.1.0, so step 1 fails until then. Until then, the [store-and-recall sample](samples.md) runs from source.
:::

This page wires Mnemosyne into a console app. It remembers a procedure, recalls it for a new question and records a run. It takes about ten minutes.

You need the .NET 10 SDK, Docker, and an embedding model. The steps below use [Ollama](https://ollama.com), which runs locally. If you'd rather not install a model at all, the [store-and-recall sample](samples.md) does the same with a built-in stand-in and needs only Docker.

## 1. Install the packages

```bash
dotnet new console -n MnemosyneQuickstart
cd MnemosyneQuickstart
dotnet add package Mnemosyne.NET
dotnet add package Mnemosyne.NET.PostgreSql
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package OllamaSharp
```

- `Mnemosyne.NET` holds recall and ranking, and brings in `Mnemosyne.NET.Abstractions`.
- `Mnemosyne.NET.PostgreSql` is the store.
- `Microsoft.Extensions.DependencyInjection` gives the console app a service container. An ASP.NET Core or generic host already has one.
- `OllamaSharp` is the embedding model's client. Any `IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI works in its place.

## 2. Start PostgreSQL with pgvector

```bash
docker run -d --name mnemosyne-pg \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=mnemosyne \
  -p 5432:5432 pgvector/pgvector:pg17
```

Mnemosyne needs PostgreSQL 15 or later with pgvector 0.8.0 or later. The `pgvector/pgvector:pg17` image has both.

Then pull the embedding model:

```bash
ollama pull nomic-embed-text
```

`nomic-embed-text` returns vectors of 768 dimensions. The store needs that number.

## 3. Register the services

Replace `Program.cs` with this:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;
using OllamaSharp;

const string connectionString =
    "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=mnemosyne";

var services = new ServiceCollection();

// The store, on a data source the container owns and disposes.
services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 768 });

// The embedding model. Dimensions above must match what it returns.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text"));

// The facade: recall, ranking and remember.
services.AddMnemosyne();

await using var provider = services.BuildServiceProvider();
var ct = CancellationToken.None;
```

Three registrations make up Mnemosyne:

- **`AddMnemosynePostgreSql`** registers the store. It takes the `NpgsqlDataSource` from the container. `Dimensions` is the length of every embedding, and it is fixed the first time the schema is created.
- **The embedding generator** turns questions into vectors. Use the same model for remembering and recalling.
- **`AddMnemosyne`** registers `IReasonletMemory`, the facade you call. With no argument it uses the default options. Register the data source and the generator as singletons, because the facade is one.

### What the registrations do

`AddMnemosyne` registers:

- `IReasonletMemory`, as a singleton `ReasonletMemory`;
- the `MnemosyneOptions`;
- the default `IReasonletSelector`, built from `MnemosyneOptions.Ranking`, unless you registered your own;
- `TimeProvider.System`, unless you registered your own clock.

It doesn't register a store or an embedding generator. If the container lacks an `IReasonletStore` or an `IEmbeddingGenerator<string, Embedding<float>>`, the first resolve of `IReasonletMemory` throws `InvalidOperationException`. An `IIntentExtractor` and an `IReasonletEvaluator` are optional: register one and the facade uses it.

**Register everything the facade uses as a singleton:** the store, the embedding generator, the intent extractor, the evaluator and any selector of your own. The facade resolves them once, for the life of the container. A scoped registration fails under scope validation, which ASP.NET Core turns on in Development, and a transient one is captured for good anyway.

Both registrations take their options as an object, not a configure delegate, because the members are init-only, and `Dimensions` is `required`. Both check the options when you register: `AddMnemosyne` throws `ArgumentException` for an invalid value, and `AddMnemosynePostgreSql` throws `ArgumentOutOfRangeException`. [PostgreSQL store](guides/postgresql.md#options) and [Ranking](concepts/ranking.md#settings) list the options.

## 4. Run the migrations

```csharp
var migrated = await provider.GetRequiredService<ReasonletPostgreSqlStore>().MigrateAsync(ct);
if (migrated.IsFailure)
{
    throw new InvalidOperationException($"{migrated.Error.Code}: {migrated.Error.Message}");
}
```

The host runs the migrations; the store never runs them on its own. Call `MigrateAsync` once at startup. It is safe to call again, because applied migrations are skipped. It creates a `mnemosyne` schema, and on a fresh database it also creates the `vector` extension, which needs a superuser. The `postgres` user above is one.

Mnemosyne reports expected failures as results rather than exceptions. Check a result's `IsSuccess` or `IsFailure`, then read its `Value` or its `Error`. An error has a `Code`, one of `Validation`, `NotFound`, `Conflict` and `Unavailable`, and a `Message`.

## 5. Remember a procedure

After your agent solves a problem, remember how it did so. The procedure is a graph of steps that typically ends in an answer.

```csharp
var memory = provider.GetRequiredService<IReasonletMemory>();
var scope = new ReasonletScope("tenant-42");
var provenance = new ReasonletProvenance { Source = "support-agent" };

var graph = new ReasonletGraph
{
    SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
    Nodes =
    [
        new ReasonletNode { Id = "find-order", Type = "tool", Tool = "orders.get" },
        new ReasonletNode { Id = "refund", Type = "tool", Tool = "payments.refund" },
        new ReasonletNode { Id = "answer", Type = "answer" },
    ],
    Edges =
    [
        new ReasonletEdge { From = "find-order", To = "refund" },
        new ReasonletEdge { From = "refund", To = "answer" },
    ],
};

var remembered = await memory.RememberAsync(
    new ReasonletCapture
    {
        Scope = scope,
        Question = "How do I refund an order for a customer?",
        Graph = graph,
        Outcome = new ReasonletOutcome(Success: true, Duration: TimeSpan.FromSeconds(2), Score: null),
        Provenance = provenance,
    },
    ct);
if (remembered.IsFailure)
{
    throw new InvalidOperationException($"{remembered.Error.Code}: {remembered.Error.Message}");
}

Console.WriteLine($"Stored {remembered.Value.Kind}, version {remembered.Value.Version}.");
```

This prints `Stored NewReasonlet, version 1.0.`

- **The scope** is the partition the procedure lives in, usually a tenant. Recall only ever searches one scope. See [Scopes and subjects](concepts/scopes-and-subjects.md).
- **The outcome** is the result of the run the procedure came from. It counts as the first run.
- **Name and intent** are optional. Without them, the name is the question's first line, and the intent comes from an intent extractor if you registered one, or is `unspecified`.

## 6. Recall for a new question

```csharp
var recalled = await memory.RecallAsync(
    new ReasonletQuery { Scope = scope, Question = "How do I refund an order?" },
    ct);
if (recalled.IsFailure)
{
    throw new InvalidOperationException($"{recalled.Error.Code}: {recalled.Error.Message}");
}

foreach (var match in recalled.Value)
{
    var score = match.Score;
    Console.WriteLine(
        $"{match.Reasonlet.Name} {match.Reasonlet.Version}: total {score.Total:F3} " +
        $"(similarity {score.Similarity:F3}, success {score.SuccessRate:F3}, freshness {score.Freshness:F3})");
}
```

Recall returns up to three matches by default, best first. Each match holds the stored Reasonlet, including its graph, and a score with all its parts. [Ranking](concepts/ranking.md) explains the score.

An empty list is a success: nothing was similar enough to pass the cut-off, so the agent works from scratch.

## 7. Record each run

When the agent follows a recalled procedure, record how it went. The track record is what lets a reliable procedure outrank a merely similar one.

```csharp
if (recalled.Value is [var best, ..])
{
    // The agent runs best.Reasonlet.Graph in its own way, then reports the outcome.
    var outcome = new ReasonletOutcome(Success: true, Duration: TimeSpan.FromSeconds(3), Score: null);
    var run = new ReasonletRun { Scope = scope, Id = best.Reasonlet.Id, Outcome = outcome, Provenance = provenance };
    var recorded = await memory.RecordExecutionAsync(run, ct);
    if (recorded.IsFailure)
    {
        throw new InvalidOperationException($"{recorded.Error.Code}: {recorded.Error.Message}");
    }

    // No evaluator is registered, so the agent's own verdict counts: Source is Agent.
    Console.WriteLine($"Recorded run {recorded.Value.Execution}: {recorded.Value.Source} says {recorded.Value.Success}");
}
```

The outcome is the agent's own view of the run. The result is a receipt: the run's execution id, which feedback needs, and the verdict that counted, its `Source` and `Success`. To have an `IReasonletEvaluator` judge the run instead, register one and set the run's `Question` and `Answer`. The answer goes to the evaluator and is never stored. A judge that is down or times out, or an answer that is blank or longer than 32,000 code points, doesn't fail the call: the agent's verdict counts, and the receipt's `Source` is `Agent`. When a person later says whether the run worked, record it with `RecordFeedbackAsync` and `Worked`, which overrides both.

If the agent changed the procedure and the new one worked better, remember it with `ParentId` set to the recalled Reasonlet's id instead. That stores a new version, which [Versions and lineage](concepts/versions.md) describes.

## 8. Run it

```bash
dotnet run
```

The output shows the stored Reasonlet, then the match with its score, then the recorded run with the verdict that counted, `Agent` here. Its similarity depends on the model. Its success rate is 0.583, from one successful run smoothed toward a neutral 0.5, and its freshness is close to 1 because it ran moments ago.

Run it a second time and the remember stores another Reasonlet, so recall returns two matches: without a `ParentId`, every capture is a new one.

To have each run start clean, add an erase of the scope at the end of `Program.cs`:

```csharp
var erased = await memory.EraseAsync(new ReasonletErasure(scope, SubjectId: null), ct);
if (erased.IsFailure)
{
    throw new InvalidOperationException($"{erased.Error.Code}: {erased.Error.Message}");
}
```

With it, every run leaves the database empty and prints one match.

When you are done, remove the container so the sample's `docker compose`, which also uses port 5432, does not clash with it:

```bash
docker rm -f mnemosyne-pg
```

## Next steps

- [The store-and-recall sample](samples.md) builds a track record, captures a new version and shows it ranking first.
- [Reasonlets](concepts/reasonlets.md) covers what a capture holds.
- [Ranking](concepts/ranking.md) covers the cut-off, the weights and how to tune them.
- [PostgreSQL store](guides/postgresql.md) covers privileges, options and migrations in depth, and [Data lifecycle](guides/data-lifecycle.md) covers erasure and pruning.
