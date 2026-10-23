# Store and recall

A console walkthrough of Mnemosyne.NET. It needs only Docker and the .NET 10 SDK, and no API key.

It stores three procedures, records how they ran, captures a new version of the proven one, and recalls a question. The output shows each step and the ranked matches with their score breakdown.

## Run it

From this folder:

```bash
docker compose up -d --wait
dotnet run
docker compose down
```

`docker compose up -d --wait` starts PostgreSQL 17 with pgvector and returns once it accepts connections. The sample connects to `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=mnemosyne_sample`, which matches the compose file. To use another server, set `MNEMOSYNE_SAMPLE_PG` to its connection string. It needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

The sample migrates the `mnemosyne` schema with 256 dimensions, so give it a database of its own. Each run works in a fresh scope and erases it at the end.

## What it does

1. Registers an `NpgsqlDataSource`, the store with `AddMnemosynePostgreSql`, an embedding generator and the facade with `AddMnemosyne`, then runs `MigrateAsync`.
2. Remembers three procedures with `RememberAsync`. Each remember records the first run, and `RecordExecutionAsync` records the rest:
   - "refund an order", proven: 20 successful runs out of 20;
   - "refund a partial order", a close match with a different question: 5 out of 10;
   - a new version of "refund an order": the same question, a different graph, `ParentId` set to the original, and one successful run.
3. Recalls "How do I refund an order?" with `RecallAsync` and prints the top 3.
4. Checks that the new version ranks first and the original second, and exits 1 if not. CI runs the sample, so this is tested.
5. Erases the scope with `EraseAsync`.

The output:

```text
  #  Reasonlet                Version  Similarity  Success  Freshness   Total
  1  refund an order          1.1           0.816    0.917      1.000   0.883
  2  refund an order          1.0           0.816    0.900      1.000   0.878
  3  refund a partial order   1.0           0.904    0.500      1.000   0.802
```

Recall ranks by 0.5 × similarity + 0.3 × success rate + 0.2 × freshness:

- **Success rate** is smoothed: (successes + 5 × prior) / (runs + 5). The original's prior is 0.5, so 20 of 20 gives 22.5 / 25 = 0.900.
- **A new version inherits its parent's record** as its prior: max(0.900, 0.5) = 0.900. Its one successful run gives (1 + 4.5) / 6 = 0.917, so it ranks just above the original it refines.
- **The close match** scores the highest similarity, but its 5-of-10 record ranks it last.
- **Freshness** is 1.000 because every procedure ran moments ago.

The [Mnemosyne.NET README](../../src/Mnemosyne.NET/README.md#ranking) explains the ranking in full.

## The embedding generator is a stand-in

`HashingEmbeddingGenerator` lets the sample run offline. It lowercases the text, splits it into words, hashes each word into one of 256 buckets and normalises the result. It is deterministic, so the ranking check is stable, but **it is not a real model**: it sees only shared words. "Refund an order" and "return a purchase" look unrelated to it. Do not judge recall quality by it.

In an application, register a real model through Microsoft.Extensions.AI and set `Dimensions` to the model's output size. With [Ollama](https://ollama.com) and the OllamaSharp package, whose `OllamaApiClient` is an embedding generator:

```csharp
using OllamaSharp;

// ollama pull nomic-embed-text; it returns 768 dimensions.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text"));
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 768 });
```

With OpenAI and the Microsoft.Extensions.AI.OpenAI package, whose `AsIEmbeddingGenerator` adapts the OpenAI client:

```csharp
using OpenAI;

// text-embedding-3-small returns 1536 dimensions.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OpenAIClient(apiKey).GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 1536 });
```

A real model gives different similarities, so the numbers above change. Tune the similarity cut-off for your model, as the [cut-off section](../../src/Mnemosyne.NET/README.md#the-cut-off) describes. A database migrated with one dimension count cannot take another, so use a new database when you switch.
