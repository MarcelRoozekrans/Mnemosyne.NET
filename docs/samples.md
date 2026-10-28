---
title: Samples
sidebar_position: 3
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
4. Checks the full order (new version, original, close match) and the totals shown below, and exits with code 1 if not.
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
  1  refund an order          1.1           0.816    0.848      1.000   0.863
  2  refund an order          1.0           0.816    0.833      1.000   0.858
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

## Evaluation

[`samples/Mnemosyne.Samples.Evaluation`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.Evaluation) is a console app that shows how a run's verdict changes the ranking. It needs only Docker and the .NET 10 SDK. It needs no API key and no model.

### What it does

1. Registers the PostgreSQL store, an embedding generator, a rule-based evaluator and the facade, and runs the migrations. The evaluator calls an answer a success when it holds a refund reference like `REF-4821` and the word "notified".
2. Remembers two procedures for near-identical questions, so their similarity is equal and the success rate decides:
   - **"refund by hand"**: 8 runs, none with an answer, so the agent's verdict counts for each;
   - **"refund through the API"**: 5 runs, each with an answer that meets the rule, so the evaluator judges each.
3. Recalls "How do I refund an order?" three times. Between the recalls, one run on "refund through the API" is recorded and then given feedback.
4. Checks every receipt, the order after each act, and the totals, evidence and success rates shown below, and exits with code 1 if not.
5. Erases the scope, so a run leaves nothing behind.

### Run it

Clone the repository, then from the sample's folder:

```bash
cd samples/Mnemosyne.Samples.Evaluation
docker compose up -d --wait
dotnet run
docker compose down
```

The database listens on port 5433, so it can run next to the store-and-recall sample's. To use another server, set the `MNEMOSYNE_SAMPLE_PG` environment variable to its connection string. The server needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

### The output

Three recalls, one after each act:

```text
Act 1. Every answer for "refund through the API" meets the rule, so its runs count in full.
  #  Reasonlet                Similarity  Success   Total  Runs  Judged
  1  refund through the API        0.926    0.750   0.888     5       5
  2  refund by hand                0.926    0.722   0.880     8       0

Act 2. One more run on "refund through the API": the agent reports success, and the answer has a refund reference but does not say the customer was notified.
  Receipt: source Evaluator, success False, execution <execution id>. The agent said true; the evaluator said false.
  #  Reasonlet                Similarity  Success   Total  Runs  Judged
  1  refund by hand                0.926    0.722   0.880     8       0
  2  refund through the API        0.926    0.682   0.867     6       6

Act 3. A person checks and says the customer was in fact notified: feedback with Worked true on that run overrides the evaluator.
  #  Reasonlet                Similarity  Success   Total  Runs  Judged
  1  refund through the API        0.926    0.773   0.895     6       6
  2  refund by hand                0.926    0.722   0.880     8       0
```

The execution id in the act 2 receipt is different on every run, so it is shown as a placeholder.

- **Act 1.** Five judged successes give a success rate of 0.750. "Refund by hand" has 8 runs and none judged, so it ranks second.
- **Act 2.** The agent said the run worked, but the evaluator overruled it. The failed judged run drops the rate to 0.682, and "refund by hand" ranks first.
- **Act 3.** Feedback with `Worked` true overrides the evaluator, so the run counts as a success and the rate rises to 0.773.

[Evaluation](concepts/evaluation.md) explains who decides whether a run worked, and [Ranking](concepts/ranking.md#success-rate) works through the success rate.

The sample checks all of this itself, and the repository's CI runs it. If a change to judging or ranking broke what this page shows, CI would fail.

### The evaluator is a stand-in

A word check is not how to judge real answers. In an application, implement `IReasonletEvaluator` over whatever decides whether an answer worked. The [custom evaluator guide](guides/custom-evaluator.md) shows the contract.

The sample also uses the same offline embedding stand-in as store and recall, so [the same caveat applies](#the-embedding-generator-is-a-stand-in).
