---
title: Samples
sidebar_position: 5
---

# Samples

## Store and recall

[`samples/Mnemosyne.Samples.StoreAndRecall`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.StoreAndRecall) is a console app that walks through the whole loop. It needs only Docker and the .NET 10 SDK. It needs no API key and no embedding model.

### What it does

1. Wires up the PostgreSQL store, an embedding generator and the Mnemosyne facade, and runs the migrations.
2. Remembers three procedures in a fresh scope, and records how they ran:
   - **"refund an order"**, proven: 20 successful runs out of 20;
   - **"refund a partial order"**, a close match for the question it will recall, but with 5 successes in 10 runs;
   - **a new version of "refund an order"**: the same question with an improved graph, which adds a refund-window check and a customer email, remembered with `ParentId` set to the original, and one successful run.
3. Recalls "How do I refund an order?" and prints the top three matches with their score breakdown.
4. Checks that the new version ranks first and the original second, and exits with code 1 if not.
5. Erases the scope, so a run leaves nothing behind.

### Run it

Clone the repository, then from the sample's folder:

```bash
cd samples/Mnemosyne.Samples.StoreAndRecall
docker compose up -d --wait
dotnet run
docker compose down
```

`docker compose up -d --wait` starts PostgreSQL 17 with pgvector and returns once the database accepts connections.

The sample connects to `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=mnemosyne_sample`, which matches the compose file. To use another server, set the `MNEMOSYNE_SAMPLE_PG` environment variable to its connection string. The server needs PostgreSQL 15 or later with pgvector 0.8.0 or later. The sample migrates the `mnemosyne` schema with 256 dimensions, so give it a database of its own.

### The output

The heart of the output is the ranked list:

```text
  #  Reasonlet                Version  Similarity  Success  Freshness   Total
  1  refund an order          1.1           0.816    0.917      1.000   0.883
  2  refund an order          1.0           0.816    0.900      1.000   0.878
  3  refund a partial order   1.0           0.904    0.500      1.000   0.802
```

- The new version ranks first after a single run, because it inherits its parent's record.
- The close match has the highest similarity, but its 5-in-10 record ranks it last.

[Ranking](concepts/ranking.md#a-worked-example) works through every number in this table.

The sample checks this order itself, and the repository's CI runs it on every pull request to `main` and every push to `main`. If a change to the ranking broke what this page shows, CI would fail.

### The embedding generator is a stand-in

To run offline, the sample ships `HashingEmbeddingGenerator`. It lowercases the text, splits it into words, hashes each word into one of 256 buckets and normalises the result. It is deterministic, so the ranking check is stable.

**It is not a real model.** It sees only shared words: "refund an order" and "return a purchase" look unrelated to it. Don't judge recall quality by it.

In an application, register a real model through Microsoft.Extensions.AI, and set the store's `Dimensions` to the model's output size. With [Ollama](https://ollama.com) and the OllamaSharp package:

```csharp
using OllamaSharp;

// ollama pull nomic-embed-text; it returns 768 dimensions.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text"));
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 768 });
```

With OpenAI and the Microsoft.Extensions.AI.OpenAI package:

```csharp
using OpenAI;

// text-embedding-3-small returns 1536 dimensions.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OpenAIClient(apiKey).GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 1536 });
```

A real model gives different similarities, so the numbers above change, and you should [tune the cut-off](concepts/ranking.md#the-cut-off) for it. A database migrated with one dimension count can't take another, so use a new database when you switch models.
